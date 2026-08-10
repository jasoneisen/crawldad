using Crawldad.Contracts.Runs;

namespace Crawldad.Web.Features.Runs;

/// <summary>The durable, resumable position persisted at a <c>checkpoint</c>. Held in <see cref="RunProgress"/>, not the
/// immutable trace, because the var snapshot is bulk accumulated state. Both JSON payloads are stored as scrubbed raw
/// text and re-parsed on resume — sufficient, with the checkpoint's <c>resume</c> sub-program, to continue without refetching earlier work.</summary>
public sealed record StoredCheckpoint(string Name, int Sequence, int StepIndex, string CursorJson, string VarsJson);

/// <summary>The per-run read model that backs <c>GET /runs/{id}</c> and the executor's resume. Distinct from the run's
/// event stream and the orchestration <see cref="RunExecutorSaga"/>: a mutable, deletable document the executor solely
/// owns, advancing <see cref="Checkpoint"/> durably mid-run and stamping the terminal disposition + result at the end.</summary>
public sealed class RunProgress
{
    /// <summary>The run id (the document id).</summary>
    public Guid Id { get; set; }

    /// <summary>The current disposition: <c>running</c> until the executor finalises it, then a terminal status.</summary>
    public RunStatus Status { get; set; } = RunStatus.Running;

    /// <summary>The last durably-recorded checkpoint, or null before the first checkpoint / for a non-checkpointing run.</summary>
    public StoredCheckpoint? Checkpoint { get; set; }

    /// <summary>The scrubbed evaluated <c>result</c> as raw JSON (set once <see cref="RunStatus.Succeeded"/>), else null.</summary>
    public string? ResultJson { get; set; }

    /// <summary>The scrubbed salvaged partial result as raw JSON (set once <see cref="RunStatus.Cancelled"/>), else null.</summary>
    public string? PartialJson { get; set; }

    /// <summary>The typed failure (set once <see cref="RunStatus.Failed"/>), else null.</summary>
    public RunFailureDetail? Failure { get; set; }

    /// <summary>The run counters (set at any terminal status), else null.</summary>
    public RunStats? Stats { get; set; }

    /// <summary>How long the run waited in the admission queue before it started, in milliseconds: set at promotion for
    /// a run that was <see cref="RunStatus.Queued"/>, null for a run started immediately. A plain, queryable document
    /// field, so a tenant's p95 queue wait is computable from stored data without a metrics library.</summary>
    public long? QueueWaitMs { get; set; }
}
