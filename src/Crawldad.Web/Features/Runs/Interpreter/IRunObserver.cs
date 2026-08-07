using System.Text.Json;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>
/// The durable-execution seam between the interpreter and the executor saga (§11/§14.2). The synchronous
/// <c>POST /runs</c> path passes <c>null</c> (no observer), so its behaviour — and every existing golden — is unchanged:
/// a <c>checkpoint</c> node is then an inert no-op and cancellation is never signalled. The background executor supplies
/// an observer that (a) persists each reached checkpoint to durable saga state so a killed run can resume, and (b)
/// surfaces a cooperative cancel the interpreter honours <b>between steps</b>. The observer owns all persistence; the
/// interpreter stays a pure execution engine that only reports.
/// </summary>
internal interface IRunObserver
{
    /// <summary>Whether a cooperative cancel has been requested for this run (§11). Read between steps; a true value tears
    /// the run down cleanly at the next node boundary and reports <c>cancelled</c> with a partial result.</summary>
    bool CancellationRequested { get; }

    /// <summary>
    /// Appends one trace event to the run's stream durably and notifies live subscribers (§13/§11). The interpreter emits
    /// its semantic step-trace events (<c>StepStarted</c>/<c>Navigated</c>/<c>Extracted</c>/…) and its coarse
    /// <c>LogEmitted</c>/<c>RunAttemptFailed</c> events through here on the executor path, in occurrence order; the executor
    /// scrubs each at the <see cref="RunEventScrubber"/> chokepoint (§12), commits it from its own Marten session (so a
    /// tailing SSE client sees it immediately), and pings the low-latency notification. The synchronous path has no observer,
    /// so it emits no step events and accumulates its coarse events for the endpoint to append — behaviour and goldens unchanged.
    /// </summary>
    /// <param name="traceEvent">The trace event to append (scrubbed by the observer).</param>
    /// <param name="ct">Cancels the append.</param>
    ValueTask EmitAsync(object traceEvent, CancellationToken ct);

    /// <summary>Records a reached checkpoint durably (§11): the executor persists it to saga state (cursor + var snapshot)
    /// and appends the metadata-only trace event, so a process death resumes from here with a fresh browser session.</summary>
    /// <param name="checkpoint">The checkpoint's name, sequence, enclosing top-level step index, cursor, and var snapshot.</param>
    /// <param name="ct">Cancels the persistence.</param>
    ValueTask CheckpointReachedAsync(CheckpointSnapshot checkpoint, CancellationToken ct);
}

/// <summary>
/// One reached checkpoint (§11): everything the executor persists so a killed run can re-establish and continue and
/// produce the same final result. <see cref="StepIndex"/> is the enclosing <b>top-level</b> step (the loop the checkpoint
/// heads), so resume re-enters exactly there; <see cref="Cursor"/> is the payload-declared resume position (a page cursor
/// / URL); <see cref="Vars"/> is the accumulated declared-var state (opaque handles excluded — they are re-derived).
/// </summary>
/// <param name="Name">The checkpoint's stable name.</param>
/// <param name="Sequence">A monotonic per-run counter (ordering; survives resume via <see cref="ResumeState.Sequence"/>).</param>
/// <param name="StepIndex">The top-level step index the checkpoint's loop occupies — the resume re-entry point.</param>
/// <param name="Cursor">The evaluated cursor value (JSON) — bound to the <c>checkpoint</c> var on resume.</param>
/// <param name="Vars">The snapshot of accumulated declared vars (JSON) — restored into the fresh run scope on resume.</param>
internal sealed record CheckpointSnapshot(string Name, int Sequence, int StepIndex, JsonElement Cursor, JsonElement Vars);

/// <summary>
/// The state a resumed run re-establishes from (§11), built by the executor from the saga's last
/// <see cref="CheckpointSnapshot"/>. The interpreter restores <see cref="Vars"/> into a fresh scope, binds
/// <see cref="Cursor"/> to the <c>checkpoint</c> var, and re-enters execution at <see cref="StepIndex"/>; the first
/// <c>checkpoint</c> node hit there runs its <c>resume</c> sub-program to re-navigate the fresh session to the cursor,
/// then execution continues normally — producing the same result as an uninterrupted run, without refetching earlier work.
/// </summary>
/// <param name="CheckpointName">The resumed checkpoint's name (observability).</param>
/// <param name="Sequence">The sequence to continue from, so post-resume checkpoints keep climbing monotonically.</param>
/// <param name="StepIndex">The top-level step to re-enter at.</param>
/// <param name="Cursor">The restored cursor value (JSON), bound to the <c>checkpoint</c> var for the <c>resume</c> block.</param>
/// <param name="Vars">The restored declared-var snapshot (JSON).</param>
internal sealed record ResumeState(string CheckpointName, int Sequence, int StepIndex, JsonElement Cursor, JsonElement Vars);
