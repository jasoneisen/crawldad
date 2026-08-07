using Crawldad.Contracts;
using Crawldad.Web.Features.Payloads;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Security;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
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

/// <summary>
/// All host wiring lives here (not in Program.cs) so that booting the app through Alba exercises — and
/// therefore covers — every line of configuration. Crawldad is API-only: a JSON service, no Blazor.
/// </summary>
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

                // Each vertical slice self-registers its events/projections on the shared lifecycle: the Payloads
                // aggregate + summary read model (§14.1) and the Run aggregate + step-trace/timeline read models
                // (§14.2), each filling its module in place exactly as IncidentModule does in the foundation.
                PayloadsModule.ConfigureMarten(options, projectionLifecycle);
                RunsModule.ConfigureMarten(options, projectionLifecycle);
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
        // foundation; the Runs slice registers the POST /runs validator and the browser-backend seam (including the
        // credential scrubber + per-run secret scope, §12), and the Payloads slice registers the POST /payloads validator.
        RunsModule.AddRunsServices(builder.Services);
        PayloadsModule.AddPayloadsServices(builder.Services);
        ScrubAllLogOutput(builder.Services);
        return builder;
    }

    // The Wolverine message pipeline (§14): transactional outbox, durable local queues (so the executor saga's messages
    // survive restarts, §11), bus-side validation, and the resume-not-dead-letter policy for an interrupted run.
    private static void ConfigureWolverine(WolverineOptions options)
    {
        options.Policies.AutoApplyTransactions();
        options.Policies.UseDurableLocalQueues();

        // Validate on the bus too (only for message types that HAVE a validator), so in-process callers get the same
        // guards as the HTTP API — not just its endpoints. ExplicitRegistration: validators are AddScoped in their slice
        // modules; letting Wolverine re-discover them would double-register each one and force the multi-validator path.
        options.UseFluentValidation(RegistrationBehavior.ExplicitRegistration);
    }

    // Route ALL log output through the credential scrubber (§12): decorate the host's ILoggerFactory so every category's
    // logger — application, Wolverine, Marten, ASP.NET — scrubs its rendered message before any sink writes it. Wrapping
    // the factory (the single point every ILogger/ILogger<T> is created from) is the central chokepoint, not per-call-site
    // discipline; the inner factory is built from the resolved provider set at first use, so providers registered after
    // this point are wrapped too. CredentialScrubber is registered by RunsModule.AddRunsServices above.
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

        // The JSON API. Validation failures become 400 ProblemDetails via the FluentValidation middleware.
        app.MapWolverineEndpoints(options => options.UseFluentValidationProblemDetailMiddleware());

        return app;
    }
}
