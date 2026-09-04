using Crawldad.Api.Features.Billing;
using Crawldad.Api.Features.Browsers;
using Crawldad.Api.Features.Fixtures;
using Crawldad.Api.Features.Payloads;
using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Tenancy;
using Crawldad.Api.Features.Webhooks;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Api.Infrastructure.Storage;
using Crawldad.Contracts;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.FluentValidation;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;
using Wolverine.Marten;

namespace Crawldad.Api;

/// <summary>All host wiring lives here (not in Program.cs) so that booting the app through Alba exercises — and
/// therefore covers — every line of configuration. Crawldad is API-only: a JSON service, no Blazor.</summary>
public static class HostConfiguration
{
    // Single config key selecting the projection lifecycle for BOTH aggregate snapshots and read models across
    // every slice. A wrong VALUE throws loudly at boot; a mistyped KEY would silently fall back to Async — so the
    // reader here and the test-side writer (UseCrawldadTestDefaults) share this one const. Mirrors the foundation.
    public const string ProjectionLifecycleKey = "Crawldad:ProjectionLifecycle";

    public static WebApplicationBuilder AddCrawldadPlatform(this WebApplicationBuilder builder)
    {
        builder.Host.ApplyJasperFxExtensions(); // surfaces Marten's db-*/projections CLI commands

        var projectionLifecycle = builder.Configuration.GetValue(ProjectionLifecycleKey, ProjectionLifecycle.Async);

        var marten = builder.Services.AddMarten(options =>
            {
                options.Connection(builder.Configuration.GetConnectionString("marten")!);
                // Schema isolation so Crawldad coexists with other apps on the shared devcontainer Postgres.
                options.DatabaseSchemaName = "crawldad";

                // Per-tenant data isolation via Marten's native conjoined multi-tenancy: one shared schema with every
                // event stream and document row qualified by a tenant_id, every session opened for a tenant.
                options.Policies.AllDocumentsAreMultiTenanted();
                options.Events.TenancyStyle = TenancyStyle.Conjoined;

                // Each vertical slice self-registers its events/projections on the shared lifecycle (Payloads + Run
                // aggregates and their read models), filling its module in place exactly as IncidentModule does.
                PayloadsModule.ConfigureMarten(options, projectionLifecycle);
                RunsModule.ConfigureMarten(options, projectionLifecycle);
                BrowsersModule.ConfigureMarten(options); // the tenant-scoped browser-credential document (no projection)
                FixturesModule.ConfigureMarten(options); // the tenant-scoped recorded fixture-set document (no projection)
                WebhooksModule.ConfigureMarten(options); // the tenant-scoped webhook-endpoint registration document (no projection)
                ManagementModule.ConfigureMarten(options); // the SINGLE-tenanted registry documents (tenants + hashed api keys)
                BillingModule.ConfigureMarten(options); // the SINGLE-tenanted processed-webhook-event dedup document
            })
            .IntegrateWithWolverine()           // transactional outbox/inbox + aggregate handlers
            .AddAsyncDaemon(DaemonMode.HotCold);

        // Diff/apply the schema on boot only as a dev convenience; prod/CI applies it out-of-band via
        // `dotnet run -- db-apply`, so we don't do blocking schema I/O on every production start.
        if (builder.Environment.IsDevelopment())
        {
            marten.ApplyAllDatabaseChangesOnStartup();
        }

        builder.Host.UseWolverine(ConfigureWolverine);

        builder.Services.AddWolverineHttp();

        // Serialize enums as strings on the wire, via the convention shared with any typed client (Wolverine.Http
        // honours these options). No enums cross the wire yet, but the seam is wired for the first slice that adds one.
        builder.Services.ConfigureHttpJsonOptions(o => ContractsJson.Configure(o.SerializerOptions));

        // Time is a DI seam (the Alba fixture swaps in a fake). Register the BCL clock and hand the SAME provider
        // to Marten so event-metadata timestamps honour it too — real time in prod, frozen in tests.
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.ConfigureMarten((sp, opts) => opts.Events.TimeProvider = sp.GetRequiredService<TimeProvider>());

        // ProblemDetails for unhandled exceptions, paired with UseExceptionHandler in the production branch below.
        builder.Services.AddProblemDetails();

        // Per-slice service registration, then the tenant security boundary and the log-scrubbing decorator.
        AddFeatureServices(builder);
        AddTenantSecurity(builder);
        ScrubAllLogOutput(builder.Services);
        return builder;
    }

    // Each vertical slice self-registers its DI (validators + infrastructure seams), mirroring the foundation; e.g. Runs
    // registers the POST /runs validator + browser-backend seam, Payloads its POST /payloads validator. Grouped here so
    // the composition root stays legible as slices are added.
    private static void AddFeatureServices(WebApplicationBuilder builder)
    {
        RunsModule.AddRunsServices(builder.Services);
        PayloadsModule.AddPayloadsServices(builder.Services);
        BrowsersModule.AddBrowsersServices(builder.Services); // browser-registration store + encrypting resolver
        FixturesModule.AddFixturesServices(builder.Services); // tenant fixture-set store + the `fixture` replay backend
        WebhooksModule.AddWebhooksServices(builder.Services); // webhook-endpoint store + HTTP sender + delivery options
        BillingModule.AddBillingServices(builder); // tier catalog + provider gateway (fake in dev/tests, fail-closed stub in prod) + dedup store
        StorageModule.AddStorage(builder.Services, builder.Configuration); // durable download sink + screenshot store + retention janitor
        DataProtectionModule.AddKeyRingProtection(builder.Services, builder.Configuration); // the at-rest secret cipher + its persisted key ring
    }

    // The tenant boundary: the config-bound tenant directory + the machine-to-machine API-key scheme, plus the
    // authorization services RequireAuthorizeOnAll leans on. Authentication resolves the tenant/actor claims; the tenant
    // API keys are also folded into the credential scrubber as always-on secrets, so a key never surfaces in output.
    private static void AddTenantSecurity(WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<TenantOptions>().Bind(builder.Configuration.GetSection(TenantOptions.Section));
        builder.Services.AddSingleton<TenantRegistry>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<TenantContext>();

        // The DB-backed tenant registry: the store over the single-tenanted registry documents, and the composite
        // directory that resolves a presented key against it (cached, revocation-safe) and FALLS BACK to the env-configured
        // TenantRegistry — so existing staging/beta wiring keeps working unchanged. The same directory feeds the admission
        // gate a registry tenant's slot allowance as a per-tenant concurrency override. Interim management-key options too.
        builder.Services.AddOptions<TenantRegistryOptions>().Bind(builder.Configuration.GetSection(TenantRegistryOptions.Section));
        builder.Services.AddOptions<ManagementOptions>().Bind(builder.Configuration.GetSection(ManagementOptions.Section));
        builder.Services.AddSingleton<ITenantRegistryStore, MartenTenantRegistryStore>();
        builder.Services.AddSingleton<ITenantMembershipStore, MartenTenantMembershipStore>(); // console authorization authority (PR4)
        builder.Services.AddSingleton<IConsoleAuditStore, MartenConsoleAuditStore>();          // console-write audit trail (PR5)
        builder.Services.AddSingleton<IFreeTenantProvisioningStore, MartenFreeTenantProvisioningStore>(); // self-serve free-tenant create (PR7)

        // The console-write guard's knobs + the per-(email,tenant) sliding-window limiter (issue #119 PR5). Always registered
        // (generous defaults; the section may be omitted); inert until a console-authenticated write actually occurs, which
        // only happens when the console scheme is configured. A non-positive limit/window fails the boot.
        builder.Services.AddOptions<ConsoleWriteOptions>().Bind(builder.Configuration.GetSection(ConsoleWriteOptions.Section)).ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ConsoleWriteOptions>, ConsoleWriteOptionsValidator>();
        builder.Services.AddSingleton<ConsoleWriteRateLimiter>();

        builder.Services.AddSingleton<TenantDirectory>();
        builder.Services.AddSingleton<ITenantAuthenticator>(static sp => sp.GetRequiredService<TenantDirectory>());
        builder.Services.AddSingleton<ITenantConcurrencyOverrides>(static sp => sp.GetRequiredService<TenantDirectory>());

        builder.Services.AddAuthentication(CrawldadAuthentication.Scheme)
            .AddScheme<ApiKeyOptions, ApiKeyAuthenticationHandler>(CrawldadAuthentication.Scheme, configureOptions: null);
        builder.Services.AddAuthorization();

        // The config-gated console-principal scheme (issue #119 PR2). Registered ONLY when Crawldad:ConsoleAuth is set,
        // and as a NON-default scheme no endpoint opts into yet — so when unconfigured (every host today) it adds nothing
        // and ApiKey stays the sole/default scheme. Inert by construction: it changes zero runtime behaviour in this PR.
        ConsoleAuthModule.AddConsolePrincipal(builder.Services, builder.Configuration);

        // Replace the plain CredentialScrubber registration (RunsModule) with one that also redacts the configured tenant
        // API keys everywhere — resolved from the same bound options, so the scrubber's always-on set is the live key set.
        // Keys shorter than the scrubber's exact-match floor are inert there (and the registry rejects them at boot anyway).
        builder.Services.Replace(ServiceDescriptor.Singleton(sp => new CredentialScrubber(
            sp.GetRequiredService<IRunSecretScope>(),
            [.. sp.GetRequiredService<IOptions<TenantOptions>>().Value.Tenants.Select(tenant => tenant.ApiKey)])));
    }

    // The Wolverine message pipeline: transactional outbox, durable local queues, and bus-side validation. There is
    // deliberately NO failure policy here — an interrupted run is resumed by RunRecoveryService (Features/Runs/
    // RunExecutor.cs), an IHostedService that scans for runs still marked Running at boot and re-publishes ExecuteRun
    // for each (plus PromoteQueued for a tenant with queued runs). That covers a run caught mid-flight; it does not
    // cover a lost queue/finalize/webhook envelope, which is what the durable queues below are for.
    private static void ConfigureWolverine(WolverineOptions options)
    {
        // Discovery must never depend on Wolverine's stack-walk heuristic. AddWolverine builds WolverineOptions with no
        // assembly name, which leaves ApplicationAssembly NULL until bootstrap; if it is still null then, Wolverine walks
        // the call stack for the first assembly that is not System*/Microsoft*/a test runner/JasperFx*/dynamic (else
        // Assembly.GetEntryAssembly(), i.e. testhost) and caches the answer in the process-wide STATIC
        // WolverineOptions.RememberedApplicationAssembly, which every later host in the process then reuses — divergence
        // is only ever a logged warning (GH-3521). Both graphs discover from the one collection that value fills,
        // HandlerGraph.Discovery.Assemblies, which Wolverine.Http's HttpGraph also reads as options.Assemblies — so a
        // single wrong answer means zero message handlers AND zero HTTP endpoints. CI run 33843310346 hit exactly that on
        // a docs-only PR with unchanged product code: IndeterminateRoutesException for StartRun, 383 failures, every
        // endpoint 404, from the first host boot on. The suite is serial (maxParallelThreads 1), so it was stack-walk
        // ordering nondeterminism, not a thread race.
        //
        // Setting it here is both early enough and total: this callback is invoked synchronously inside AddWolverine,
        // BEFORE the bootstrap that would run the heuristic (WolverineOptions.ReadJasperFxOptions, at DI resolution), and
        // that bootstrap is guarded by `if (_applicationAssembly == null)` — so the heuristic never runs and no stale
        // assembly is ever filled alongside this one. The setter also hands the assembly to CodeGeneration. This is
        // Wolverine's own prescription for a test harness, quoted in its divergence warning.
        options.ApplicationAssembly = typeof(HostConfiguration).Assembly;

        options.Policies.AutoApplyTransactions();

        // Load-bearing: local queues default to BufferedInMemory, so without this an envelope enqueued and not yet
        // handled is lost on an unclean stop, with no dead-letter row and no log. This backs them with the Postgres
        // message store IntegrateWithWolverine() registers. Pinned by DurableLocalQueueConfigurationTests.
        options.Policies.UseDurableLocalQueues();

        // Validate on the bus too (only for message types that HAVE a validator), so in-process callers get the same
        // guards as the HTTP API — not just its endpoints. ExplicitRegistration: validators are AddScoped in their slice
        // modules; letting Wolverine re-discover them would double-register each one and force the multi-validator path.
        options.UseFluentValidation(RegistrationBehavior.ExplicitRegistration);
    }

    // Route ALL log output through the credential scrubber: decorate the host's ILoggerFactory so every category's
    // logger — application, Wolverine, Marten, ASP.NET — scrubs its rendered message before any sink writes it. The
    // inner factory is built from the resolved provider set at first use, so providers registered after this point are wrapped too.
    private static void ScrubAllLogOutput(IServiceCollection services) =>
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(sp => new ScrubbingLoggerFactory(
            new LoggerFactory(
                sp.GetServices<ILoggerProvider>(),
                sp.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>(),
                sp.GetService<IOptions<LoggerFactoryOptions>>()),
            sp.GetRequiredService<CredentialScrubber>())));

    public static WebApplication MapCrawldadPlatform(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            // Production: emit ProblemDetails on unhandled exceptions and enforce HSTS. Development gets the
            // framework's developer exception page instead.
            app.UseExceptionHandler();
            app.UseHsts();
        }

        // The tenant boundary: authenticate the API key into a tenant/actor principal, then authorize. Ordered
        // before the endpoints so an unauthenticated request never reaches a handler.
        app.UseAuthentication();
        app.UseAuthorization();

        // The console-write guard (issue #119 PR5): after authorization (so it only sees requests that passed ConsoleOrKey),
        // before the endpoint — it rate-limits + audits console-authenticated writes and passes everything else through. Inert
        // on a host where the console scheme is unconfigured (no request can be console-authenticated).
        app.UseMiddleware<ConsoleWriteAuditMiddleware>();

        // The JSON API. Validation failures become 400 ProblemDetails via the FluentValidation middleware.
        app.MapWolverineEndpoints(options =>
        {
            options.UseFluentValidationProblemDetailMiddleware();

            // Every Wolverine endpoint requires an authenticated tenant — no anonymous mutating or reading route
            // survives. /health opts out with [AllowAnonymous] (a liveness probe must answer an unauthenticated load
            // balancer); the endpoint-enumeration test asserts every other route rejects an unauthenticated request.
            options.RequireAuthorizeOnAll();

            // The enumerated console scope: exactly the ConsoleReadEndpoints GET routes (issue #119 PR4) AND the
            // ConsoleWriteEndpoints (method, route) writes (PR5) ALSO accept a console principal, via the ConsoleOrKey policy
            // layered on top of the blanket gate above. Every other endpoint keeps the default ApiKey-only policy. When
            // Crawldad:ConsoleAuth is unconfigured the ConsoleOrKey policy is ApiKey-only, so these endpoints are byte-for-byte
            // as they are today. The enumeration test pins the live set (reads + writes, as separate lists).
            options.ConfigureEndpoints(ApplyConsoleScope);

            // Scope each request's Marten session to the tenant on the authenticated principal. Wolverine opens the
            // injected IDocumentSession/IQuerySession for this tenant and stamps it onto messages the endpoint publishes,
            // so the async run path carries the tenant to the executor saga without any explicit plumbing.
            options.TenantId.IsClaimTypeNamed(CrawldadClaims.TenantId);
        });

        // The interim server-side management surface (tenant + key administration), on its own management-key auth rather
        // than the tenant principal — mapped only when a management key is configured, otherwise /management/* is a 404.
        ManagementModule.MapManagementEndpoints(app);

        return app;
    }

    // Applies the console authorization scope to a single mapped endpoint (issue #119 PR4–PR7): the self-serve provisioning
    // endpoint gets ConsoleProvisioning (console scheme only, no membership); an Owner-only console endpoint (key + membership
    // management) gets ConsoleOwnerOrKey; a Member-reachable console read/write gets ConsoleOrKey; every other endpoint keeps
    // the default ApiKey-only gate. Extracted from MapCrawldadPlatform to stay within the length budget.
    private static void ApplyConsoleScope(HttpChain chain)
    {
        var route = chain.RoutePattern!.RawText; // every mapped HTTP chain carries a RoutePattern; Includes handles null RawText
        if (ProvisioningEndpoints.Includes(chain.HttpMethods, route))
        {
            // Self-serve provisioning (issue #119 PR7): the console scheme ONLY, and NO membership required — the one console
            // surface reachable before a tenant scope exists. Checked first so it never falls to a membership-requiring policy.
            chain.RequireAuthorization(ConsoleAuthModule.ProvisioningPolicy);
        }
        else if (ConsoleOwnerEndpoints.Includes(chain.HttpMethods, route))
        {
            // Owner-only: still a console write (audited, rate-limited), but the console channel additionally requires the
            // Owner role; an API key is unrestricted. Checked first so an Owner route never falls to the Member-reachable policy.
            chain.RequireAuthorization(ConsoleAuthModule.ConsoleOwnerOrKeyPolicy);
        }
        else if (ConsoleReadEndpoints.Includes(chain.HttpMethods, route)
            || ConsoleWriteEndpoints.Includes(chain.HttpMethods, route))
        {
            chain.RequireAuthorization(ConsoleAuthModule.ConsoleOrKeyPolicy);
        }
    }
}
