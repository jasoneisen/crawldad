using Crawldad.Contracts.Runs;

namespace Crawldad.Api.Features.Runs;

/// <summary>The durable, resumable position persisted at a <c>checkpoint</c>. Held in <see cref="RunProgress"/>, not the
/// immutable trace, because the var snapshot is bulk accumulated state. Both JSON payloads are stored as raw text scrubbed
/// through the result-channel posture (<c>CredentialScrubber.ScrubJson</c> — exact-secret redaction, but NOT the
/// credential-param rule, so a <c>token=</c>-shaped extracted value or cursor URL a resumed run restores is not corrupted;
/// issue #82) and re-parsed on resume — sufficient, with the checkpoint's <c>resume</c> sub-program, to continue without refetching earlier work.</summary>
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

    /// <summary>When the run's result body was persisted at finalisation — the clock the result-retention sweep ages a
    /// stored <see cref="ResultJson"/>/<see cref="PartialJson"/> against (the <see cref="RunProgress"/> analogue of a
    /// blob's last-modified). Set for every run that reached terminal through the executor/finaliser
    /// (<see cref="RunFinalization"/>); null while <c>running</c>/<c>queued</c>, and for a run that terminated while
    /// still queued (cancel/timeout), which persists no result body to expire.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>When the result-retention sweep aged this run's stored <see cref="ResultJson"/>/<see cref="PartialJson"/>
    /// body out (nulling it), else null. The terminal status + stats stay queryable via <c>GET /runs/{id}</c>, so this is
    /// the clear "the result body expired" marker the poll surfaces instead of a bare null or a 404. Never set by the
    /// on-demand <c>DELETE /runs/{id}</c> erasure, which removes the whole document (the poll then 404s).</summary>
    public DateTimeOffset? ResultExpiredAt { get; set; }

    /// <summary>How long the run waited in the admission queue before it started, in milliseconds: set at promotion for
    /// a run that was <see cref="RunStatus.Queued"/>, null for a run started immediately. A plain, queryable document
    /// field, so a tenant's p95 queue wait is computable from stored data without a metrics library.</summary>
    public long? QueueWaitMs { get; set; }
}
