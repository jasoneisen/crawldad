using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Security;
using Marten;

namespace Crawldad.Web.Features.Runs;

/// <summary>The shared terminal-finalisation of a run onto the async surface, used by both the durable executor and the
/// sync auto-upgrade supervisor so an upgraded run reaches byte-for-byte the same terminal disposition a native async run
/// would. Deletes the run's <see cref="RunExecutorSaga"/> in the <b>same transaction</b> as the terminal disposition, so a finished run's script+inputs are reclaimed atomically — no crash window in which the saga lingers.</summary>
internal static class RunFinalization
{
    /// <summary>Applies <paramref name="outcome"/> to the loaded <paramref name="progress"/> and the run's stream on
    /// <paramref name="session"/>. The caller loads the running progress row, then commits + notifies after this returns.</summary>
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
        // event (in occurrence order). Empty on the executor path (its observer already appended them live); populated
        // on the sync upgrade path (the observer-less synchronous engine buffered them for replay).
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

        // Free the admission slot as the run finalises — BEFORE the terminal status commits — so a caller that then
        // observes "terminal" can immediately start another run without a transient false 429. Idempotent (callers repeat it).
        gate.Release(tenantId, runId);
        session.Store(progress);

        // Complete the durable orchestration saga by deleting it IN THIS SAME TRANSACTION as the terminal disposition:
        // the run's script+inputs are reclaimed atomically with it reaching terminal, so no crash window exists between
        // the terminal commit and cleanup. A no-op if there is no saga (the sync fast-path never starts one).
        session.Delete<RunExecutorSaga>(runId);
    }
}
