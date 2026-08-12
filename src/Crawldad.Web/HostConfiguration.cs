using Crawldad.Contracts;
using Crawldad.Web.Features.Browsers;
using Crawldad.Web.Features.Fixtures;
using Crawldad.Web.Features.Payloads;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Security;
using Crawldad.Web.Infrastructure.Storage;
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

namespace Crawldad.Web;

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

        // Per-slice service registration (validators + infrastructure seams). Each slice owns its DI, mirroring the
        // foundation; e.g. Runs registers the POST /runs validator + browser-backend seam, Payloads its POST /payloads validator.
        RunsModule.AddRunsServices(builder.Services);
        PayloadsModule.AddPayloadsServices(builder.Services);
        BrowsersModule.AddBrowsersServices(builder.Services); // browser-registration store + encrypting resolver
        FixturesModule.AddFixturesServices(builder.Services); // tenant fixture-set store + the `fixture` replay backend
        StorageModule.AddStorage(builder.Services, builder.Configuration); // durable download sink + screenshot store + retention janitor
        DataProtectionModule.AddKeyRingProtection(builder.Services, builder.Configuration); // the at-rest secret cipher + its persisted key ring
        AddTenantSecurity(builder);
        ScrubAllLogOutput(builder.Services);
        return builder;
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

        builder.Services.AddAuthentication(CrawldadAuthentication.Scheme)
            .AddScheme<ApiKeyOptions, ApiKeyAuthenticationHandler>(CrawldadAuthentication.Scheme, configureOptions: null);
        builder.Services.AddAuthorization();

        // Replace the plain CredentialScrubber registration (RunsModule) with one that also redacts the configured tenant
        // API keys everywhere — resolved from the same bound options, so the scrubber's always-on set is the live key set.
        // Keys shorter than the scrubber's exact-match floor are inert there (and the registry rejects them at boot anyway).
        builder.Services.Replace(ServiceDescriptor.Singleton(sp => new CredentialScrubber(
            sp.GetRequiredService<IRunSecretScope>(),
            [.. sp.GetRequiredService<IOptions<TenantOptions>>().Value.Tenants.Select(tenant => tenant.ApiKey)])));
    }

    // The Wolverine message pipeline: transactional outbox, durable local queues (so the executor saga's messages
    // survive restarts), bus-side validation, and the resume-not-dead-letter policy for an interrupted run.
    private static void ConfigureWolverine(WolverineOptions options)
    {
        options.Policies.AutoApplyTransactions();
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

        // The JSON API. Validation failures become 400 ProblemDetails via the FluentValidation middleware.
        app.MapWolverineEndpoints(options =>
        {
            options.UseFluentValidationProblemDetailMiddleware();

            // Every Wolverine endpoint requires an authenticated tenant — no anonymous mutating or reading route
            // survives. /health opts out with [AllowAnonymous] (a liveness probe must answer an unauthenticated load
            // balancer); the endpoint-enumeration test asserts every other route rejects an unauthenticated request.
            options.RequireAuthorizeOnAll();

            // Scope each request's Marten session to the tenant on the authenticated principal. Wolverine opens the
            // injected IDocumentSession/IQuerySession for this tenant and stamps it onto messages the endpoint publishes,
            // so the async run path carries the tenant to the executor saga without any explicit plumbing.
            options.TenantId.IsClaimTypeNamed(CrawldadClaims.TenantId);
        });

        return app;
    }
}
