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
}

/// <summary>
/// The Run aggregate (§14.2): an anemic snapshot folded from the trace events, registered on the shared projection
/// lifecycle. Phase 1 tracks only identity + disposition; the executor saga, checkpoints, and the RunTimeline read
/// model arrive later. Decisions live in the endpoint/interpreter, not here.
/// </summary>
/// <param name="Id">The run id (the event stream id).</param>
/// <param name="PayloadName">The payload name pinned at start.</param>
/// <param name="ScriptHash">The script hash pinned at start.</param>
/// <param name="Status">The current lifecycle state.</param>
public sealed record Run(Guid Id, string PayloadName, string ScriptHash, RunLifecycle Status)
{
    /// <summary>Folds the opening event into a fresh aggregate (Marten assigns <see cref="Id"/> from the stream).</summary>
    /// <param name="started">The opening event.</param>
    public static Run Create(RunStarted started) => new(Guid.Empty, started.PayloadName, started.ScriptHash, RunLifecycle.Running);

    /// <summary>Marks the run succeeded.</summary>
    /// <param name="succeeded">The success event.</param>
    public Run Apply(RunSucceeded succeeded) => this with { Status = RunLifecycle.Succeeded };

    /// <summary>Marks the run failed.</summary>
    /// <param name="failed">The failure event.</param>
    public Run Apply(RunFailed failed) => this with { Status = RunLifecycle.Failed };
}
