using System.Text.Json;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>The durable-execution seam between the interpreter and the executor saga. A null observer (the synchronous
/// <c>POST /runs</c> path) makes <c>checkpoint</c> an inert no-op and cancellation never signals. A real observer (the
/// background executor) persists checkpoints and surfaces cooperative cancel; the interpreter itself only reports.</summary>
internal interface IRunObserver
{
    /// <summary>Whether a cooperative cancel has been requested for this run. Read between steps; a true value tears
    /// the run down cleanly at the next node boundary and reports <c>cancelled</c> with a partial result.</summary>
    bool CancellationRequested { get; }

    /// <summary>Appends one trace event to the run's stream durably and notifies live subscribers. The executor scrubs
    /// each event at the <see cref="RunEventScrubber"/> chokepoint and commits it from its own Marten session (so a
    /// tailing SSE client sees it immediately); the synchronous path has no observer, so behaviour is unchanged.</summary>
    ValueTask EmitAsync(object traceEvent, CancellationToken ct);

    /// <summary>Records a reached checkpoint durably: the executor persists it to saga state (cursor + var snapshot)
    /// and appends the metadata-only trace event, so a process death resumes from here with a fresh browser session.</summary>
    ValueTask CheckpointReachedAsync(CheckpointSnapshot checkpoint, CancellationToken ct);
}

/// <summary>One reached checkpoint: everything the executor persists so a killed run can re-establish and continue.
/// <see cref="StepIndex"/> is the enclosing top-level step (resume re-enters exactly there); <see cref="Vars"/> excludes
/// opaque handles (they are re-derived on resume, not restored).</summary>
internal sealed record CheckpointSnapshot(string Name, int Sequence, int StepIndex, JsonElement Cursor, JsonElement Vars);

/// <summary>The state a resumed run re-establishes from, built by the executor from the saga's last
/// <see cref="CheckpointSnapshot"/>. The interpreter restores <see cref="Vars"/>, binds <see cref="Cursor"/> to the
/// <c>checkpoint</c> var, and re-enters at <see cref="StepIndex"/> — whose <c>resume</c> sub-program re-navigates.</summary>
internal sealed record ResumeState(string CheckpointName, int Sequence, int StepIndex, JsonElement Cursor, JsonElement Vars);
