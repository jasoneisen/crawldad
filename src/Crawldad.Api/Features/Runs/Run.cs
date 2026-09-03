namespace Crawldad.Api.Features.Runs;

/// <summary>The lifecycle of a run's aggregate snapshot. Distinct from the wire <c>RunStatus</c>: a run is transiently
/// <see cref="Running"/> between <c>RunStarted</c> and its terminal event. This one never reaches the wire at all.
/// <para><b>Stored as its ordinal.</b> With no Marten serializer override the default <c>EnumStorage.AsInteger</c> is in
/// force, so the integers below — not the names — are what sits in the <see cref="Run"/> snapshot document. The explicit
/// values are an append-only contract: add a member with the next free value, <b>never renumber</b>, and retire one as a
/// tombstone that keeps its value. Pinned member-by-member in <c>EnumOrdinalContractTests</c>.</para></summary>
public enum RunLifecycle
{
    /// <summary>Started, not yet finished.</summary>
    Running = 0,

    /// <summary>Finished successfully.</summary>
    Succeeded = 1,

    /// <summary>Finished with a typed failure.</summary>
    Failed = 2,

    /// <summary>Cancelled between steps — the backend session was torn down cleanly.</summary>
    Cancelled = 3,

    /// <summary>Admitted at the concurrent-run cap and waiting for a slot — not yet executing. Declared last so the
    /// ordinal is additive for the persisted snapshot.</summary>
    Queued = 4,
}

/// <summary>The Run aggregate: an anemic snapshot folded from the trace events. Tracks identity + disposition +
/// (when pinned) the exact payload revision executed, so drift is a pure read over this snapshot and the payload head.
/// Decisions live in the endpoint/interpreter, not here.</summary>
public sealed record Run(Guid Id, string PayloadName, string ScriptHash, RunLifecycle Status, Guid? PayloadId, int? PayloadRevision)
{
    /// <summary>Folds the opening event of a run started immediately (under the cap) into a fresh aggregate (Marten assigns
    /// <see cref="Id"/> from the stream).</summary>
    public static Run Create(RunStarted started) =>
        new(Guid.Empty, started.PayloadName, started.ScriptHash, RunLifecycle.Running, started.PayloadId, started.PayloadRevision);

    /// <summary>Folds the opening event of a run <b>queued</b> at the concurrent-run cap into a fresh aggregate — the
    /// alternative stream opener to <see cref="Create(RunStarted)"/>. Becomes <see cref="RunLifecycle.Running"/> on
    /// <see cref="RunDequeued"/> when a slot frees.</summary>
    public static Run Create(RunQueued queued) =>
        new(Guid.Empty, queued.PayloadName, queued.ScriptHash, RunLifecycle.Queued, queued.PayloadId, queued.PayloadRevision);

    /// <summary>Marks a queued run promoted to execution: the queued→running transition.</summary>
    public Run Apply(RunDequeued dequeued) => this with { Status = RunLifecycle.Running };

    /// <summary>Marks the run succeeded.</summary>
    public Run Apply(RunSucceeded succeeded) => this with { Status = RunLifecycle.Succeeded };

    /// <summary>Marks the run failed.</summary>
    public Run Apply(RunFailed failed) => this with { Status = RunLifecycle.Failed };

    /// <summary>Marks the run cancelled.</summary>
    public Run Apply(RunCancelled cancelled) => this with { Status = RunLifecycle.Cancelled };
}
