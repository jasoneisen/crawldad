using Crawldad.Contracts.Runs;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Fake;
using Crawldad.Web.Infrastructure.Browser.Real;
using Crawldad.Web.Infrastructure.Security;
using FluentValidation;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// Self-registration for the Runs slice (§14.2/§14.4): the <see cref="Run"/> aggregate snapshot on the shared
/// projection lifecycle, plus the slice's DI (the <c>POST /runs</c> validator and the browser-backend seam). Phase 5
/// adds the executor saga, step-level trace events, and the RunTimeline read model here.
/// </summary>
public static class RunsModule
{
    /// <summary>Registers the <see cref="Run"/> snapshot on the shared <paramref name="lifecycle"/> (Inline under the test switch, Async in production).</summary>
    /// <param name="options">The Marten store options.</param>
    /// <param name="lifecycle">The shared projection lifecycle.</param>
    public static void ConfigureMarten(StoreOptions options, ProjectionLifecycle lifecycle)
    {
        // Both enums declare Inline=0, Async=1 (SnapshotLifecycle has no Live, which P1 never selects), so the
        // ordinal cast is a branch-free, faithful mapping from the config-driven ProjectionLifecycle.
        options.Projections.Snapshot<Run>((SnapshotLifecycle)(int)lifecycle);

        // The pure trace events (§13) carry no aggregate Apply, so Marten does not discover them from the Run
        // snapshot — register them explicitly so the schema knows the types and old streams stay readable. The P5
        // checkpoint/resume/cancellation-request markers (§11) join them; RunCancelled has a Run Apply but is listed for
        // parity so the whole terminal set is explicit. The CD-16 queue markers (RunQueued opens a queued run's stream;
        // RunDequeued is its queued→running promotion) are folded by the Run snapshot + RunTimeline, listed here for parity.
        options.Events.AddEventTypes([
            typeof(LogEmitted), typeof(RunAttemptFailed),
            typeof(RunCheckpointReached), typeof(RunResumed), typeof(RunCancellationRequested), typeof(RunCancelled),
            typeof(RunQueued), typeof(RunDequeued),
        ]);

        // The WP3 semantic step-trace events (§13): appended only on the executor path, consumed by the SSE tail and the
        // RunTimeline projection. Registered explicitly for the same reason (no aggregate Apply on the Run snapshot) so the
        // schema knows the types and old runs stay readable (§14.3 event-schema versioning).
        options.Events.AddEventTypes([
            typeof(RunSessionOpened), typeof(StepStarted), typeof(Navigated), typeof(Clicked),
            typeof(Waited), typeof(Extracted), typeof(Downloaded), typeof(Screenshotted), typeof(StepFailed), typeof(Filled),
        ]);

        // The RunTimeline observability read model (§13): the ordered step list + durations + refs + region, folded from the
        // step trace on the shared lifecycle (async in production — the lag-tolerant dashboard view, §11; inline under the test switch).
        options.Projections.Add<RunTimelineProjection>(lifecycle);

        // The executor-owned run-progress read model (§11): the pollable state + the durable resume cursor. A plain Marten
        // document (not a projection) written solely by the executor's own sessions.
        options.Schema.For<RunProgress>();

        // The durable FIFO admission-queue entry (CD-16): a plain, tenant-scoped Marten document — one per run waiting at the
        // concurrent-run cap — so the queue survives process restarts. Written at enqueue, deleted at promotion/cancel/timeout.
        options.Schema.For<QueuedRun>();
    }

    /// <summary>Registers the slice's services: the request validator and the browser-backend registry + P1 fake.</summary>
    /// <param name="services">The DI container.</param>
    public static void AddRunsServices(IServiceCollection services)
    {
        services.AddScoped<IValidator<StartRunRequest>, StartRunRequestValidator>();

        // The server-side resource limits (CD-3/CD-16/§12): the config knobs bound from Crawldad:Limits, boot-validated so a
        // nonsensical value fails the host loudly at startup (the per-tenant overrides are already boot-validated in the tenant
        // registry). The four mid-run caps flow into the interpreter as RunLimits; the admission gate reads
        // MaxConcurrentRunsPerTenant and the run queue reads MaxQueueDepthPerTenant/MaxQueueWaitMs. A payload can never raise them.
        services.AddOptions<RunLimitsOptions>().BindConfiguration(RunLimitsOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<RunLimitsOptions>, RunLimitsOptionsValidator>();

        // The concurrent-run admission gate (CD-3) and the durable FIFO admission queue (CD-16): both singletons so the gate's
        // per-tenant slot counts and the queue's process-monotonic FIFO sequence span every request and the background
        // executor. At the cap the endpoint enqueues rather than 429s; a freed slot promotes the tenant's oldest queued run.
        services.AddSingleton<IRunAdmissionGate, RunAdmissionGate>();
        services.AddSingleton<RunQueue>();

        // The durable-execution surface (§11/§14.2): the background executor that drives the saga's runs (owning its own
        // Marten sessions) and the in-process stop-signal registry the cancel endpoint + saga deadline raise. The saga and
        // its Marten storage are discovered/registered by Wolverine's Marten integration; these are the extra services the
        // executor handler and the control endpoints resolve.
        services.AddSingleton<IRunControlRegistry, RunControlRegistry>();
        services.AddSingleton<RunExecutor>();
        services.AddHostedService<RunRecoveryService>();

        // The CD-15 sync auto-upgrade supervisor: a singleton that drives a synchronous run past the sync cap to its terminal
        // state in-process after the endpoint returns 202, reusing the shared RunFinalization + the durable saga's deadline/recovery.
        // Also a hosted service (#26) whose StopAsync drains the in-flight tails on shutdown — the SAME singleton the endpoint
        // adopts into, so the drain sees exactly those tails — before the provider (and its singleton store/bus) is disposed.
        services.AddSingleton<SyncRunSupervisor>();
        services.AddHostedService(static sp => sp.GetRequiredService<SyncRunSupervisor>());

        // The WP3 observability surface (§13): the in-process SSE tail-wakeup hub, shared by the executor's appends and the
        // GET /runs/{id}/events endpoint. The failure-screenshot blob store (IScreenshotStore) and the download-sink seam
        // (§9.3) are wired by StorageModule (CD-2), which selects the durable/fake provider from configuration.
        services.AddSingleton<RunEventSignals>();

        // The backend seam: a registry over keyed adapters. Phase 1 registered only the record/replay fake, reading
        // shipped fixtures from the app's output directory; Phase 4 adds the three real adapters beside it.
        services.AddSingleton<IBrowserBackendRegistry, KeyedBrowserBackendRegistry>();
        services.AddKeyedSingleton<IBrowserBackend>(
            "fake",
            static (_, _) => new FakeBrowserBackend(Path.Combine(AppContext.BaseDirectory, "Fixtures")));

        AddRealBrowserBackends(services);
    }

    // The Phase 4 real backend adapters (§9) and the shared policy-layer singletons they compose. The credential-free
    // "local" adapter, the native "browserless" adapter, and the CDP "browserbase" adapter register beside the fake;
    // the Playwright driver, the cross-run asset cache, and the global throttle are process-wide singletons shared by
    // all three. Endpoint/API bases default to production and are overridable via configuration (tests point them at a
    // local Playwright server / a local session-create stub, so no live third-party traffic is ever made).
    private static void AddRealBrowserBackends(IServiceCollection services)
    {
        services.AddHttpClient(); // IHttpClientFactory for the browserbase session-create call

        // The secret-vault seam (§12/CD-6): a keyed-adapter registry, the same pattern as backends and storage targets. The
        // `config` adapter (secrets from IConfiguration) is the only one shipped; a real azure-keyvault/aws-secretsmanager/
        // hashicorp-vault/HTTP adapter is one AddKeyedSingleton line later. The plain ISecretStore singleton (the backend-connect
        // path) and the keyed `config` vault (the CD-6 form-fill secretRef path) share the one ConfigurationSecretStore instance,
        // so both resolution surfaces read the same Secrets section.
        services.AddSingleton<ConfigurationSecretStore>();
        services.AddSingleton<ISecretStore>(static sp => sp.GetRequiredService<ConfigurationSecretStore>());
        services.AddKeyedSingleton<ISecretStore>(SecretVaults.Config, static (sp, _) => sp.GetRequiredService<ConfigurationSecretStore>());
        services.AddSingleton<ISecretStoreRegistry, KeyedSecretStoreRegistry>();

        // The credential-scrubbing boundary (§12, WP3): the per-run secret registry the adapters register the resolved
        // credential into, and the single scrubber every sink (events, response, logs) consults. Registered here so any
        // container that wires the adapters (the host, the DI-registration unit test) also has the scope they resolve.
        services.AddSingleton<IRunSecretScope, AmbientRunSecretScope>();
        services.AddSingleton<CredentialScrubber>();

        services.AddSingleton<IPlaywrightProvider, PlaywrightProvider>();
        services.AddSingleton<IAssetCache, InMemoryAssetCache>();
        // Throttling is inherently wall-clock — pin the system clock so a frozen test TimeProvider never freezes it.
        services.AddSingleton<IThrottleGate>(static _ => new ThrottleGate(TimeProvider.System));

        services.AddKeyedSingleton<IBrowserBackend>("local", static (sp, _) => new LocalChromiumBackend(
            sp.GetRequiredService<IPlaywrightProvider>(),
            sp.GetRequiredService<IAssetCache>(),
            sp.GetRequiredService<IThrottleGate>()));

        services.AddKeyedSingleton<IBrowserBackend>("browserless", static (sp, _) => new BrowserlessBackend(
            sp.GetRequiredService<IPlaywrightProvider>(),
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<IRunSecretScope>(),
            sp.GetRequiredService<IAssetCache>(),
            sp.GetRequiredService<IThrottleGate>(),
            sp.GetRequiredService<IConfiguration>()["Crawldad:Browserless:EndpointTemplate"]
                ?? BrowserlessBackend.DefaultEndpointTemplate));

        services.AddKeyedSingleton<IBrowserBackend>("browserbase", static (sp, _) => new BrowserbaseBackend(
            sp.GetRequiredService<IPlaywrightProvider>(),
            sp.GetRequiredService<ISecretStore>(),
            sp.GetRequiredService<IRunSecretScope>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IAssetCache>(),
            sp.GetRequiredService<IThrottleGate>(),
            sp.GetRequiredService<IConfiguration>()["Crawldad:Browserbase:ApiBaseUrl"]
                ?? BrowserbaseBackend.DefaultApiBaseUrl));
    }
}
