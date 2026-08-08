using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Security;
using Marten;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// The shared terminal-finalisation of a run onto the async surface (§11), used by both the durable executor
/// (<see cref="RunExecutor"/>) and the CD-15 sync auto-upgrade supervisor (<see cref="SyncRunSupervisor"/>) so an upgraded
/// run reaches <b>byte-for-byte the same terminal disposition</b> a native async run would. It appends the interpreter's
/// buffered coarse trace events (empty on the executor path — its observer already appended them live; non-empty on the
/// upgrade path — the lean synchronous engine buffered them) and the terminal event to the run's Marten stream, stamps the
/// executor-owned <see cref="RunProgress"/> read model with the scrubbed disposition (§12), frees the run's admission
/// slot (CD-3), and <b>deletes the run's <see cref="RunExecutorSaga"/></b> (CD-5) — all on a caller-owned session, which the
/// caller commits and then notifies subscribers from. Because the saga delete rides the <b>same transaction</b> as the
/// terminal disposition, a finished run's script+inputs are reclaimed atomically with the run reaching terminal: no separate
/// cleanup message that a crash (or a spent, already-fired deadline) could lose, and no window in which the saga lingers
/// (SECURITY.md "Durable state at rest"). A non-finalised run is never deleted, so its saga survives for resume.
/// </summary>
internal static class RunFinalization
{
    /// <summary>Applies <paramref name="outcome"/> to the loaded <paramref name="progress"/> and the run's stream on
    /// <paramref name="session"/>. The caller loads the running progress row, then commits + notifies after this returns.</summary>
    /// <param name="session">The caller-owned Marten session (already scoped to the run's tenant).</param>
    /// <param name="runId">The run being finalised.</param>
    /// <param name="tenantId">The run's tenant (the slot to free).</param>
    /// <param name="outcome">The interpreter outcome (success/failure/cancel + buffered coarse events + stats).</param>
    /// <param name="stopReason">Why the run stopped when cancelled: a deadline maps to a terminal failure (§8.4).</param>
    /// <param name="progress">The loaded running <see cref="RunProgress"/> row to stamp with the terminal disposition.</param>
    /// <param name="scrubber">The credential scrubber (§12): every persisted string funnels through it.</param>
    /// <param name="gate">The admission gate whose slot the finalised run frees (CD-3).</param>
    /// <param name="clock">The time seam for the terminal event timestamp.</param>
    public static void Apply(
        IDocumentSession session,
        Guid runId,
        string tenantId,
        RunOutcome outcome,
        RunStopReason? stopReason,
        RunProgress progress,
        CredentialScrubber scrubber,
        IRunAdmissionGate gate,
        TimeProvider clock)
    {
        progress.Stats = outcome.Stats;

        // The interpreter's coarse LogEmitted/RunAttemptFailed events, scrubbed, land between RunStarted and the terminal
        // event (in occurrence order). Empty on the executor path (its observer already appended them live), so this is a
        // no-op there; populated on the CD-15 upgrade path (the observer-less synchronous engine buffered them for replay).
        foreach (var traceEvent in outcome.Events)
        {
            session.Events.Append(runId, RunEventScrubber.Scrub(traceEvent, scrubber));
        }

        switch (outcome.Status)
        {
            case RunStatus.Succeeded:
                progress.Status = RunStatus.Succeeded;
                progress.ResultJson = scrubber.ScrubJson(outcome.Result)!.Value.GetRawText(); // Result is non-null on success
                session.Events.Append(runId, new RunSucceeded(outcome.Stats, clock.GetUtcNow()));
                break;

            case RunStatus.Failed:
                var failure = RunEventScrubber.ScrubFailure(outcome.Failure!, scrubber);
                progress.Status = RunStatus.Failed;
                progress.Failure = failure;
                session.Events.Append(runId, new RunFailed(failure, outcome.Stats, clock.GetUtcNow()));
                break;

            case RunStatus.Cancelled when stopReason == RunStopReason.Deadline:
                var deadline = new RunFailureDetail("terminal", RunExecutor.DeadlineExceededCode, "the run exceeded its wall-clock deadline (§8.4)", new RunStepRef(0, "run"));
                progress.Status = RunStatus.Failed;
                progress.Failure = deadline;
                session.Events.Append(runId, new RunFailed(deadline, outcome.Stats, clock.GetUtcNow()));
                break;

            default: // RunStatus.Cancelled — a cooperative user cancel
                progress.Status = RunStatus.Cancelled;
                progress.PartialJson = scrubber.ScrubJson(outcome.Partial)?.GetRawText();
                session.Events.Append(runId, new RunCancelled(outcome.Stats, clock.GetUtcNow()));
                break;
        }

        // Free the admission slot as the run finalises — BEFORE the terminal status commits — so a caller that then observes
        // "terminal" can immediately start another run without a transient false 429 (CD-3). Idempotent (callers repeat it).
        gate.Release(tenantId, runId);
        session.Store(progress);

        // Complete the durable orchestration saga by deleting it IN THIS SAME TRANSACTION as the terminal disposition (CD-5):
        // the run's script+inputs are reclaimed atomically with it reaching terminal, so they never linger and no crash window
        // exists between the terminal commit and cleanup. A no-op if there is no saga (the sync fast-path never starts one).
        session.Delete<RunExecutorSaga>(runId);
    }
}
