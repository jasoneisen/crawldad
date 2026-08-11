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

/// <summary>Self-registration for the Runs slice: the <see cref="Run"/> aggregate snapshot on the shared projection
/// lifecycle, plus the slice's DI — the executor saga, trace events, RunTimeline read model, and the browser-backend seam.</summary>
public static class RunsModule
{
    /// <summary>Registers the <see cref="Run"/> snapshot on the shared <paramref name="lifecycle"/> (Inline under the test switch, Async in production).</summary>
    public static void ConfigureMarten(StoreOptions options, ProjectionLifecycle lifecycle)
    {
        // Both enums declare Inline=0, Async=1 (SnapshotLifecycle has no Live, which is never selected here), so the
        // ordinal cast is a branch-free, faithful mapping from the config-driven ProjectionLifecycle.
        options.Projections.Snapshot<Run>((SnapshotLifecycle)(int)lifecycle);

        // Events with no aggregate Apply aren't discovered by Marten from the Run snapshot — register them explicitly so
        // the schema knows the types and old streams stay readable. RunCancelled/RunQueued/RunDequeued DO have an Apply
        // but are listed too, for parity, so the whole terminal/queue event set is explicit.
        options.Events.AddEventTypes([
            typeof(LogEmitted), typeof(RunAttemptFailed), typeof(RunConnectAttemptFailed),
            typeof(RunCheckpointReached), typeof(RunResumed), typeof(RunCancellationRequested), typeof(RunCancelled),
            typeof(RunQueued), typeof(RunDequeued),
        ]);

        // The semantic step-trace events: appended only on the executor path, consumed by the SSE tail and the
        // RunTimeline projection. Registered explicitly for the same reason (no aggregate Apply on the Run snapshot),
        // so the schema knows the types and old runs stay readable.
        options.Events.AddEventTypes([
            typeof(RunSessionOpened), typeof(StepStarted), typeof(Navigated), typeof(Clicked),
            typeof(Waited), typeof(Extracted), typeof(Downloaded), typeof(Screenshotted), typeof(Captured), typeof(SelectorMiss), typeof(StepFailed), typeof(Filled),
        ]);

        // The RunTimeline observability read model: the ordered step list + durations + refs + region, folded from the
        // step trace on the shared lifecycle (async in production — the lag-tolerant dashboard view; inline under the test switch).
        options.Projections.Add<RunTimelineProjection>(lifecycle);

        // The executor-owned run-progress read model: the pollable state + the durable resume cursor. A plain Marten
        // document (not a projection) written solely by the executor's own sessions.
        options.Schema.For<RunProgress>();

        // The durable FIFO admission-queue entry: a plain, tenant-scoped Marten document — one per run waiting at the
        // concurrent-run cap — so the queue survives process restarts. Written at enqueue, deleted at promotion/cancel/timeout.
        options.Schema.For<QueuedRun>();
    }

    /// <summary>Registers the slice's services: the request validator and the browser-backend registry + fake.</summary>
    public static void AddRunsServices(IServiceCollection services)
    {
        services.AddScoped<IValidator<StartRunRequest>, StartRunRequestValidator>();

        // The server-side resource limits: the config knobs bound from Crawldad:Limits, boot-validated so a nonsensical
        // value fails the host loudly at startup. The four mid-run caps flow into the interpreter as RunLimits; the
        // admission gate reads MaxConcurrentRunsPerTenant and the run queue reads MaxQueueDepthPerTenant/MaxQueueWaitMs.
        services.AddOptions<RunLimitsOptions>().BindConfiguration(RunLimitsOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<RunLimitsOptions>, RunLimitsOptionsValidator>();

        // The concurrent-run admission gate and the durable FIFO admission queue: both singletons so the gate's
        // per-tenant slot counts and the queue's process-monotonic FIFO sequence span every request and the background
        // executor. At the cap the endpoint enqueues rather than 429s; a freed slot promotes the tenant's oldest queued run.
        services.AddSingleton<IRunAdmissionGate, RunAdmissionGate>();
        services.AddSingleton<RunQueue>();

        // The durable-execution surface: the background executor that drives the saga's runs (owning its own Marten
        // sessions) and the in-process stop-signal registry the cancel endpoint + saga deadline raise. The saga and its
        // storage are discovered/registered by Wolverine's Marten integration.
        services.AddSingleton<IRunControlRegistry, RunControlRegistry>();
        services.AddSingleton<RunExecutor>();
        services.AddHostedService<RunRecoveryService>();

        // The sync auto-upgrade supervisor: a singleton that drives a synchronous run past the sync cap to its terminal
        // state in-process after the endpoint returns 202. Also a hosted service whose StopAsync drains in-flight tails on
        // shutdown — registered as the SAME singleton the endpoint adopts into, so the drain sees exactly those tails.
        services.AddSingleton<SyncRunSupervisor>();
        services.AddHostedService(static sp => sp.GetRequiredService<SyncRunSupervisor>());

        // The observability surface: the in-process SSE tail-wakeup hub, shared by the executor's appends and the
        // GET /runs/{id}/events endpoint. The failure-screenshot blob store and the download-sink seam are wired by
        // StorageModule, which selects the durable/fake provider from configuration.
        services.AddSingleton<RunEventSignals>();

        // The backend seam: a registry over keyed adapters. The record/replay fake reads shipped fixtures from the app's
        // output directory; the real adapters are registered beside it.
        services.AddSingleton<IBrowserBackendRegistry, KeyedBrowserBackendRegistry>();
        services.AddKeyedSingleton<IBrowserBackend>(
            "fake",
            static (_, _) => new FakeBrowserBackend(Path.Combine(AppContext.BaseDirectory, "Fixtures")));

        AddRealBrowserBackends(services);
    }

    // The real backend adapters and the shared policy-layer singletons they compose: the Playwright driver, cross-run
    // asset cache, and global throttle are process-wide singletons shared by all three adapters. Endpoint/API bases are
    // overridable via configuration so tests point at local stubs — no live third-party traffic is ever made.
    private static void AddRealBrowserBackends(IServiceCollection services)
    {
        services.AddHttpClient(); // IHttpClientFactory for the browserbase session-create call

        // The secret-vault seam: a keyed-adapter registry, the same pattern as backends and storage targets. Only the
        // `config` adapter (secrets from IConfiguration) ships. The plain ISecretStore singleton backs the form-fill path
        // and the connect resolver's tenant-namespaced config fallback (Secrets:{tenant}:{ref}); both share ONE instance.
        services.AddSingleton<ConfigurationSecretStore>();
        services.AddSingleton<ISecretStore>(static sp => sp.GetRequiredService<ConfigurationSecretStore>());
        services.AddKeyedSingleton<ISecretStore>(SecretVaults.Config, static (sp, _) => sp.GetRequiredService<ConfigurationSecretStore>());
        services.AddSingleton<ISecretStoreRegistry, KeyedSecretStoreRegistry>();

        // The credential-scrubbing boundary: the per-run secret registry the adapters register the resolved credential
        // into, and the single scrubber every sink (events, response, logs) consults. Registered here so any container
        // that wires the adapters also has the scope they resolve.
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
            sp.GetRequiredService<IConnectCredentialResolver>(),
            sp.GetRequiredService<IRunSecretScope>(),
            sp.GetRequiredService<IAssetCache>(),
            sp.GetRequiredService<IThrottleGate>(),
            sp.GetRequiredService<IConfiguration>()["Crawldad:Browserless:EndpointTemplate"]
                ?? BrowserlessBackend.DefaultEndpointTemplate));

        services.AddKeyedSingleton<IBrowserBackend>("browserbase", static (sp, _) => new BrowserbaseBackend(
            sp.GetRequiredService<IPlaywrightProvider>(),
            sp.GetRequiredService<IConnectCredentialResolver>(),
            sp.GetRequiredService<IRunSecretScope>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IAssetCache>(),
            sp.GetRequiredService<IThrottleGate>(),
            sp.GetRequiredService<IConfiguration>()["Crawldad:Browserbase:ApiBaseUrl"]
                ?? BrowserbaseBackend.DefaultApiBaseUrl));
    }
}
