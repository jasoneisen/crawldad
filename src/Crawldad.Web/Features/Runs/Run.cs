namespace Crawldad.Web.Features.Runs;

/// <summary>The lifecycle of a run's aggregate snapshot. Distinct from the wire <c>RunStatus</c>: a run is transiently
/// <see cref="Running"/> between <c>RunStarted</c> and its terminal event (always both, in Phase 1's synchronous
/// execution).</summary>
public enum RunLifecycle
{
    /// <summary>Started, not yet finished.</summary>
    Running,

    /// <summary>Finished successfully.</summary>
    Succeeded,

    /// <summary>Finished with a typed failure.</summary>
    Failed,

    /// <summary>Cancelled between steps (§11) — the backend session was torn down cleanly.</summary>
    Cancelled,
}

/// <summary>
/// The Run aggregate (§14.2): an anemic snapshot folded from the trace events, registered on the shared projection
/// lifecycle. Tracks identity + disposition + (when pinned) the exact payload revision the run executed, so drift
/// (pinned-vs-head, §14.1) is a pure read over this snapshot and the payload head. Observability rides the same stream via
/// the <see cref="RunTimeline"/> read model, distinct from this snapshot. Decisions live in the endpoint/interpreter, not here.
/// </summary>
/// <param name="Id">The run id (the event stream id).</param>
/// <param name="PayloadName">The payload name pinned at start.</param>
/// <param name="ScriptHash">The script hash pinned at start.</param>
/// <param name="Status">The current lifecycle state.</param>
/// <param name="PayloadId">The pinned managed payload (§14.2), or null for an inline run.</param>
/// <param name="PayloadRevision">The pinned payload revision (§14.2), or null for an inline run.</param>
public sealed record Run(Guid Id, string PayloadName, string ScriptHash, RunLifecycle Status, Guid? PayloadId, int? PayloadRevision)
{
    /// <summary>Folds the opening event into a fresh aggregate (Marten assigns <see cref="Id"/> from the stream).</summary>
    /// <param name="started">The opening event.</param>
    public static Run Create(RunStarted started) =>
        new(Guid.Empty, started.PayloadName, started.ScriptHash, RunLifecycle.Running, started.PayloadId, started.PayloadRevision);

    /// <summary>Marks the run succeeded.</summary>
    /// <param name="succeeded">The success event.</param>
    public Run Apply(RunSucceeded succeeded) => this with { Status = RunLifecycle.Succeeded };

    /// <summary>Marks the run failed.</summary>
    /// <param name="failed">The failure event.</param>
    public Run Apply(RunFailed failed) => this with { Status = RunLifecycle.Failed };

    /// <summary>Marks the run cancelled (§11).</summary>
    /// <param name="cancelled">The cancellation event.</param>
    public Run Apply(RunCancelled cancelled) => this with { Status = RunLifecycle.Cancelled };
}
