using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Crawldad.Api.Features.Runs.Interpreter;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace Crawldad.Api.Features.Runs;

/// <summary>Everything the sync auto-upgrade hands off to <see cref="SyncRunSupervisor"/> when a synchronous run crosses
/// the sync cap: the run's identity + tenant, the <b>already-running</b> interpreter task, and the three request-scoped
/// lifetimes the supervisor now owns to run to the end — the stop control, the cancellation source, and the secret scope.</summary>
internal sealed record SyncRunHandoff(
    Guid RunId,
    string TenantId,
    Task<RunOutcome> Execution,
    RunControl Control,
    CancellationTokenSource RunCts,
    IDisposable SecretScope);

/// <summary>Drives a synchronous run that outran the sync cap to its terminal state <b>in-process</b>, after the endpoint
/// already returned <c>202</c>. Awaits the still-running interpreter (never restarting it) then finalises via the shared
/// <see cref="RunFinalization"/>. Also an <see cref="IHostedService"/>: <see cref="StopAsync"/> drains in-flight tails on shutdown BEFORE the provider is disposed, avoiding a fire-and-forget race with a disposing store/bus.</summary>
public sealed class SyncRunSupervisor(
    IDocumentStore store,
    CredentialScrubber scrubber,
    IRunAdmissionGate admissionGate,
    RunEventSignals signals,
    IRunControlRegistry controls,
    IServiceScopeFactory scopeFactory,
    TimeProvider clock) : IHostedService
{
    /// <summary>The terminal failure code for an upgraded run whose interpreter faulted unexpectedly after the 202 (the sync
    /// path would have surfaced this as a 500; on the async surface it must become a terminal failure, never a stuck run).</summary>
    public const string InternalErrorCode = "internal_error";

    /// <summary>How long <see cref="StopAsync"/> waits for in-flight tails to finalise before letting the host dispose the
    /// provider. Bounded so a run still executing at shutdown cannot hang teardown (left <c>running</c>, recovered on the
    /// next host); comfortably under the host's default 30 s shutdown timeout, so the drain finishes gracefully.</summary>
    private static readonly TimeSpan _drainTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The adopted tails still driving a run to its terminal state, keyed by run id, so host shutdown can drain
    /// them. A run is added at <see cref="Adopt"/> and removes itself when its tail ends; since its interpreter provably
    /// outran the sync window (still running at adoption), the tail can never complete before it is added.</summary>
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();

    /// <summary>Set once host shutdown begins: the freed slot's queue-promotion nudge is idempotent and durably
    /// re-triggered by the startup recovery scan, so during shutdown it is skipped rather than published onto a
    /// stopping bus. Saga cleanup is unaffected — the finaliser already deleted the saga before this skip.</summary>
    private volatile bool _shuttingDown;

    /// <summary>Adopts an upgraded run's in-flight execution and drives it to a terminal state in the background. Unlike
    /// a bare fire-and-forget, the tail is <b>tracked</b> so host shutdown can drain it. Invoked on the request's
    /// execution context so the run's ambient secret scope flows to the finaliser.</summary>
    internal void Adopt(SyncRunHandoff handoff) => _inFlight[handoff.RunId] = DriveToTerminalAsync(handoff);

    private async Task DriveToTerminalAsync(SyncRunHandoff handoff)
    {
        try
        {
            var outcome = await ResolveOutcomeAsync(handoff);
            await FinalizeAsync(handoff, outcome);
            await PromoteAsync(handoff.TenantId);
        }
        finally
        {
            // Free the run's admission slot no matter how the tail ended: RunFinalization already released it on the
            // normal path, but a throw during finalisation setup would otherwise leak it until restart — Release is
            // idempotent, so this never double-frees. Secrets are cleared AFTER finalisation so the scrub above still sees them.
            admissionGate.Release(handoff.TenantId, handoff.RunId);
            controls.Remove(handoff.RunId);
            signals.Remove(handoff.RunId);
            handoff.SecretScope.Dispose();
            handoff.RunCts.Dispose();
            _inFlight.TryRemove(handoff.RunId, out _); // stop tracking: this tail is done, so shutdown need not drain it
        }
    }

    // Awaits the in-flight interpreter to an outcome, mapping the two forcible interruptions the async surface can raise
    // after upgrade: a POST /cancel or the saga's deadline cancels the bound source (OperationCanceledException, its
    // `await using` tore the session down cleanly), and any other fault becomes a terminal internal_error.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An upgraded run has already returned 202; any unexpected fault must become a terminal internal_error so the run never stays stuck 'running' on the async surface (the sync path would have surfaced it as a 500).")]
    private static async Task<RunOutcome> ResolveOutcomeAsync(SyncRunHandoff handoff)
    {
        try
        {
            return await handoff.Execution;
        }
        catch (OperationCanceledException)
        {
            // Stopped outcome: the control's stop reason drives the cancel-vs-deadline mapping at finalisation.
            return new RunOutcome(RunStatus.Cancelled, null, null, null, EmptyStats, []);
        }
        catch (Exception)
        {
            return new RunOutcome(
                RunStatus.Failed,
                null,
                new RunFailureDetail("terminal", InternalErrorCode, "the run failed with an unexpected error after auto-upgrade", new RunStepRef(0, "run")),
                null,
                EmptyStats,
                []);
        }
    }

    private static RunStats EmptyStats => new(0, 0, 0, 0, 0, 0);

    private async Task FinalizeAsync(SyncRunHandoff handoff, RunOutcome outcome)
    {
        await using var session = store.LightweightSession(handoff.TenantId);
        var progress = (await session.LoadAsync<RunProgress>(handoff.RunId))!; // seeded "running" at upgrade, so it exists
        RunFinalization.Apply(session, handoff.RunId, handoff.TenantId, outcome, handoff.Control.StopReason, progress, scrubber, admissionGate, clock);
        await session.SaveChangesAsync();
        signals.Notify(handoff.RunId); // the terminal event closes any live SSE tail
    }

    // The freed slot promotes the tenant's oldest queued run (a no-op when none is queued). The upgraded run's saga was
    // already deleted in the finaliser's terminal transaction, so this is only about draining the queue. The request
    // that started this run is long gone, so publish on a fresh scope's bus.
    private async Task PromoteAsync(string tenantId)
    {
        if (_shuttingDown)
        {
            // Host shutdown in progress: skip the nudge rather than resolve a scope + publish onto a stopping bus. The
            // slot was already freed in the finaliser, and the startup recovery scan re-triggers PromoteQueued for every
            // tenant with a non-empty queue — so this is a deliberate, idempotent skip, not a swallowed error.
            return;
        }

        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .PublishAsync(new PromoteQueued(), new DeliveryOptions { TenantId = tenantId });
    }

    /// <summary>Hosted-service start: nothing to warm up — the supervisor only adopts tails handed to it by the endpoint.</summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Hosted-service stop = <b>drain</b>. Marks shutdown and awaits in-flight tails so their finalisation
    /// commits through the still-live singleton store <em>before</em> the host disposes the provider — the fix for the
    /// fire-and-forget teardown race. Bounded by <see cref="_drainTimeout"/>, so a stuck run can never hang shutdown.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _shuttingDown = true;

        var draining = _inFlight.Values.ToArray();
        if (draining.Length == 0)
        {
            return; // nothing adopted is still in flight — the common teardown, and every host that ran no upgraded run
        }

        using var drainWindow = new CancellationTokenSource();
        await Task.WhenAny(Task.WhenAll(draining), Task.Delay(_drainTimeout, drainWindow.Token));
        await drainWindow.CancelAsync(); // the drain finished first — stop the bounding timer (a no-op if the window already won)
    }
}
