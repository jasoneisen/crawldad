using Crawldad.Contracts;
using Crawldad.Web.Features.Payloads;
using Crawldad.Web.Features.Runs;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
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

                // Each vertical slice self-registers its events/projections on the shared lifecycle. Both are stubs
                // today (Payloads → Phase 5; Runs → a later work package); the seam is wired so those packages just
                // fill their module in place, exactly as IncidentModule does in the foundation.
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

        builder.Host.UseWolverine(options =>
        {
            options.Policies.AutoApplyTransactions();
            options.Policies.UseDurableLocalQueues();
            // Validate on the bus too (only for message types that HAVE a validator), so in-process callers get
            // the same guards as the HTTP API — not just its endpoints. ExplicitRegistration: validators are
            // AddScoped in their slice modules; letting Wolverine re-discover them would double-register each one
            // and force the multi-validator resolution path.
            options.UseFluentValidation(RegistrationBehavior.ExplicitRegistration);
        });

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
        // foundation; the Runs slice registers the POST /runs validator and the browser-backend seam, and the
        // Payloads slice registers the POST /payloads boundary validator.
        RunsModule.AddRunsServices(builder.Services);
        PayloadsModule.AddPayloadsServices(builder.Services);

        return builder;
    }

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
