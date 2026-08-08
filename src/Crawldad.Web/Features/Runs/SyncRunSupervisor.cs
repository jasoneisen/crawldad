using System.Diagnostics.CodeAnalysis;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Security;
using Marten;
using Microsoft.Extensions.DependencyInjection;
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
    TimeProvider clock)
{
    /// <summary>The terminal failure code for an upgraded run whose interpreter faulted unexpectedly after the 202 (the sync
    /// path would have surfaced this as a 500; on the async surface it must become a terminal failure, never a stuck run).</summary>
    public const string InternalErrorCode = "internal_error";

    /// <summary>Adopts an upgraded run's in-flight execution and drives it to a terminal state in the background. Fire-and-
    /// forget by design: the caller has already returned <c>202</c>, and the durable <see cref="RunExecutorSaga"/> created at
    /// upgrade is the restart/deadline backstop. Invoked on the request's execution context so the run's ambient secret
    /// scope flows to the finaliser (§12).</summary>
    /// <param name="handoff">The upgraded run's identity + its running interpreter + the lifetimes to own to the end.</param>
    internal void Adopt(SyncRunHandoff handoff) => _ = DriveToTerminalAsync(handoff);

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

    // The freed slot promotes the tenant's oldest queued run (a no-op when none is queued, CD-16). The request that started
    // this run is long gone, so publish on a fresh scope's bus — mirroring the startup recovery service's pattern.
    private async Task PromoteAsync(string tenantId)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .PublishAsync(new PromoteQueued(), new DeliveryOptions { TenantId = tenantId });
    }
}
