using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Security;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// Everything the CD-15 sync auto-upgrade hands off to <see cref="SyncRunSupervisor"/> when a synchronous run crosses the
/// sync cap: the run's identity + tenant, the <b>already-running</b> interpreter task (started inline in the request), and the
/// three request-scoped lifetimes the supervisor now owns to run to the end — the in-process stop control (cancel/deadline),
/// the interpreter's own cancellation source, and the ambient per-run secret scope (§12).
/// </summary>
/// <param name="RunId">The upgraded run.</param>
/// <param name="TenantId">The run's tenant (CD-1): the finaliser's session + the slot to free + the promotion target.</param>
/// <param name="Execution">The in-flight interpreter task started in the request — the supervisor awaits it to completion.</param>
/// <param name="Control">The in-process stop control (a POST /cancel or the saga's wall-clock deadline raises it, §8.4/§11).</param>
/// <param name="RunCts">The interpreter's cancellation source, bound to <paramref name="Control"/> as forcible-for-every-reason.</param>
/// <param name="SecretScope">The ambient per-run secret scope handle (§12): kept open across the tail, disposed at the end.</param>
internal sealed record SyncRunHandoff(
    Guid RunId,
    string TenantId,
    Task<RunOutcome> Execution,
    RunControl Control,
    CancellationTokenSource RunCts,
    IDisposable SecretScope);

/// <summary>
/// Drives a synchronous run that outran the sync cap to its terminal state <b>in-process</b> (CD-15), after the endpoint has
/// already returned <c>202 { runId, status:"running" }</c> and pinned the run onto the durable async surface. It awaits the
/// still-running interpreter (never restarting it, so no side effect is repeated), then finalises the run through the shared
/// <see cref="RunFinalization"/> exactly as the durable executor would — the same scrubbed terminal disposition a native
/// async run reaches, retrievable via <c>GET /runs/{id}</c>. It runs on the request's execution context, so the run's ambient
/// secret scope (an <see cref="AsyncLocal{T}"/>) still scrubs every finalised event (§12); the scope is disposed only once
/// the run is done. The endpoint also created the run's <see cref="RunExecutorSaga"/> at upgrade, so the wall-clock deadline
/// (its <see cref="RunDeadline"/> timeout) and restart recovery (a re-run from scratch) reuse the existing durable machinery —
/// this in-process supervisor is the happy-path completion; the saga is its backstop.
/// <para>
/// It is also an <see cref="IHostedService"/> (#26): every adopted tail is tracked, and <see cref="StopAsync"/> drains the
/// in-flight ones — bounded — on host shutdown. That runs while the singleton store and the bus are still live (a hosted
/// service stops <em>before</em> the provider is disposed), so a tail's finalisation commits cleanly instead of an untracked
/// fire-and-forget task racing a disposing provider (the intermittent <c>ObjectDisposedException</c> teardown flake, #26). A
/// run still executing when the drain window elapses stays <c>running</c> and is durably recovered by the startup recovery
/// scan on the next host — the same backstop a hard crash already relies on.
/// </para>
/// </summary>
/// <param name="store">The Marten store (the supervisor opens its own tenant-scoped session, like the executor).</param>
/// <param name="scrubber">The credential scrubber (§12): every persisted string funnels through it at finalisation.</param>
/// <param name="admissionGate">The concurrent-run gate whose slot the run frees when it finalises (CD-3).</param>
/// <param name="signals">The SSE notification hub, pinged after the terminal append so a live tail closes at once (§11).</param>
/// <param name="controls">The in-process control registry — the run's control is removed once it stops driving.</param>
/// <param name="scopeFactory">Creates a scope to resolve the (scoped) bus for the queue-promotion trigger (CD-16).</param>
/// <param name="clock">The time seam for the terminal event timestamp.</param>
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

    /// <summary>How long <see cref="StopAsync"/> waits for the in-flight tails to finalise before it lets the host proceed to
    /// dispose the provider. Bounded so a run still executing at shutdown cannot hang teardown — it is left <c>running</c> and
    /// durably recovered on the next host. Comfortably under the generic host's default 30 s shutdown timeout, so the drain
    /// gives up gracefully rather than being force-cancelled mid-commit.</summary>
    private static readonly TimeSpan _drainTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The adopted tails still driving a run to its terminal state, keyed by run id, so host shutdown can drain them
    /// (#26). A run is added at <see cref="Adopt"/> and removes itself when its tail ends; the upgrade invariant — its
    /// interpreter outran the sync window, so it is provably still running at adoption — guarantees the tail cannot complete
    /// (and self-remove) before it is added, so no entry ever leaks.</summary>
    private readonly ConcurrentDictionary<Guid, Task> _inFlight = new();

    /// <summary>Set once host shutdown begins: the post-terminal signals (the saga-completing <see cref="RunFinished"/> and the
    /// queue-promotion nudge) are both idempotent and durably re-derivable — the saga's scheduled deadline reclaims it and the
    /// startup recovery scan re-triggers promotion — so during shutdown they are skipped rather than published onto a stopping
    /// bus (#26).</summary>
    private volatile bool _shuttingDown;

    /// <summary>Adopts an upgraded run's in-flight execution and drives it to a terminal state in the background. The caller
    /// has already returned <c>202</c>, and the durable <see cref="RunExecutorSaga"/> created at upgrade is the restart/deadline
    /// backstop; unlike a bare fire-and-forget, the tail is <b>tracked</b> so host shutdown can drain it (#26). Invoked on the
    /// request's execution context so the run's ambient secret scope flows to the finaliser (§12).</summary>
    /// <param name="handoff">The upgraded run's identity + its running interpreter + the lifetimes to own to the end.</param>
    internal void Adopt(SyncRunHandoff handoff) => _inFlight[handoff.RunId] = DriveToTerminalAsync(handoff);

    private async Task DriveToTerminalAsync(SyncRunHandoff handoff)
    {
        try
        {
            var outcome = await ResolveOutcomeAsync(handoff);
            await FinalizeAsync(handoff, outcome);
            await AnnounceTerminalAsync(handoff.RunId, handoff.TenantId);
        }
        finally
        {
            // Free the run's admission slot no matter how the tail ended (CD-3): RunFinalization already released it on the
            // normal path, but a throw during finalisation setup (before that release) would otherwise leak the slot until
            // restart — Release is idempotent, so this belt-and-suspenders never double-frees. Then stop driving the run: drop
            // its in-process control + SSE slot, clear its registered secrets (§12), and dispose the interpreter's cancellation
            // source. Ordered after finalisation so the scrub above still sees the secrets.
            admissionGate.Release(handoff.TenantId, handoff.RunId);
            controls.Remove(handoff.RunId);
            signals.Remove(handoff.RunId);
            handoff.SecretScope.Dispose();
            handoff.RunCts.Dispose();
            _inFlight.TryRemove(handoff.RunId, out _); // stop tracking: this tail is done, so shutdown need not drain it (#26)
        }
    }

    // Awaits the in-flight interpreter to an outcome, mapping the two forcible interruptions the async surface can raise after
    // upgrade to a terminal disposition: a POST /cancel or the saga's wall-clock deadline (§8.4) cancels the bound source and
    // the observer-less interpreter throws OperationCanceledException (its `await using` tore the backend session down cleanly),
    // and any other unexpected fault becomes a terminal internal_error so the run never stays stuck "running".
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An upgraded run has already returned 202; any unexpected fault must become a terminal internal_error so the run never stays stuck 'running' on the async surface (the sync path would have surfaced it as a 500).")]
    private static async Task<RunOutcome> ResolveOutcomeAsync(SyncRunHandoff handoff)
    {
        try
        {
            return await handoff.Execution;
        }
        catch (OperationCanceledException)
        {
            // Stopped outcome: the control's stop reason drives the cancel-vs-deadline mapping at finalisation (§8.4/§11).
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

    private static RunStats EmptyStats => new(0, 0, 0, 0, 0);

    private async Task FinalizeAsync(SyncRunHandoff handoff, RunOutcome outcome)
    {
        await using var session = store.LightweightSession(handoff.TenantId);
        var progress = (await session.LoadAsync<RunProgress>(handoff.RunId))!; // seeded "running" at upgrade, so it exists
        RunFinalization.Apply(session, handoff.RunId, handoff.TenantId, outcome, handoff.Control.StopReason, progress, scrubber, admissionGate, clock);
        await session.SaveChangesAsync();
        signals.Notify(handoff.RunId); // the terminal event closes any live SSE tail
    }

    // Announces the upgraded run's terminal state (§14.2/CD-16): completes its durable saga at once via RunFinished — the same
    // prompt cleanup the native async executor does, reclaiming the saga's script+inputs rather than letting them linger until
    // the deadline — and promotes the tenant's oldest queued run into the freed slot (a no-op when none is queued). The request
    // that started this run is long gone, so both publish on a fresh scope's bus, mirroring the startup recovery service.
    private async Task AnnounceTerminalAsync(Guid runId, string tenantId)
    {
        if (_shuttingDown)
        {
            // Host shutdown in progress (#26): skip both rather than resolve a scope + publish onto a stopping bus. The run's
            // terminal RunProgress already committed in the finaliser, so the saga's already-scheduled RunDeadline reclaims it
            // on the next host, and the startup recovery scan re-triggers PromoteQueued for every tenant with a non-empty queue
            // — both are durably re-derivable, so this is a deliberate, idempotent skip, not a swallowed error.
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(new RunFinished(runId), new DeliveryOptions { TenantId = tenantId });
        await bus.PublishAsync(new PromoteQueued(), new DeliveryOptions { TenantId = tenantId });
    }

    /// <summary>Hosted-service start (#26): nothing to warm up — the supervisor only adopts tails handed to it by the endpoint.</summary>
    /// <param name="cancellationToken">Unused.</param>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Hosted-service stop = <b>drain</b> (#26). Marks shutdown (so tails skip the promotion nudge) and awaits the
    /// in-flight tails so their finalisation commits through the still-live singleton store <em>before</em> the host disposes
    /// the provider — the fix for the fire-and-forget teardown race. Bounded by <see cref="_drainTimeout"/>: a run still
    /// executing when the window elapses is left <c>running</c> and durably recovered by the next host's recovery scan (the
    /// same backstop a hard crash relies on), so a stuck run can never hang shutdown. Best-effort: the race outcome is not
    /// inspected — either the tails finished (the common case) or the window won, and both let teardown proceed.</summary>
    /// <param name="cancellationToken">The host's shutdown token (the drain is separately bounded, so it is not awaited on).</param>
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
