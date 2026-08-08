using Crawldad.Contracts.Runs;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// The durable, resumable position persisted at a <c>checkpoint</c> (§11). Held in the executor-owned
/// <see cref="RunProgress"/> document (not the immutable trace, §12) because the <see cref="VarsJson"/> snapshot is bulk
/// accumulated state. Both JSON payloads are stored as <b>scrubbed</b> raw text (serializer-agnostic and credential-free)
/// and re-parsed on resume. Sufficient — with a fresh navigation driven by the checkpoint's <c>resume</c> sub-program — to
/// re-establish and continue to the same final result without refetching earlier work.
/// </summary>
/// <param name="Name">The checkpoint's declared name.</param>
/// <param name="Sequence">The monotonic per-run checkpoint number (resume continues from here).</param>
/// <param name="StepIndex">The top-level step index of the checkpoint's enclosing loop — the resume re-entry point.</param>
/// <param name="CursorJson">The scrubbed cursor value as raw JSON — bound to the <c>checkpoint</c> var on resume.</param>
/// <param name="VarsJson">The scrubbed accumulated-var snapshot as raw JSON — restored into the fresh run scope on resume.</param>
public sealed record StoredCheckpoint(string Name, int Sequence, int StepIndex, string CursorJson, string VarsJson);

/// <summary>
/// The per-run read model that backs <c>GET /runs/{id}</c> and the executor's resume (§11/§14.2). Distinct from the run's
/// event stream (the immutable §13 trace) and from the orchestration <see cref="RunExecutorSaga"/>: this is a mutable,
/// deletable document the <b>executor solely owns</b> — it writes the running row up front, advances
/// <see cref="Checkpoint"/> as each checkpoint is reached (durable mid-run), and stamps the terminal disposition +
/// (scrubbed) result/partial/failure at the end. Storing the result body here — never in the trace — keeps the §12
/// invariant that events hold metadata only while a polling caller can still retrieve it.
/// </summary>
public sealed class RunProgress
{
    /// <summary>The run id (the document id).</summary>
    public Guid Id { get; set; }

    /// <summary>The current disposition: <c>running</c> until the executor finalises it, then a terminal status.</summary>
    public RunStatus Status { get; set; } = RunStatus.Running;

    /// <summary>The last durably-recorded checkpoint (§11), or null before the first checkpoint / for a non-checkpointing run.</summary>
    public StoredCheckpoint? Checkpoint { get; set; }

    /// <summary>The scrubbed evaluated <c>result</c> as raw JSON (set once <see cref="RunStatus.Succeeded"/>), else null.</summary>
    public string? ResultJson { get; set; }

    /// <summary>The scrubbed salvaged partial result as raw JSON (set once <see cref="RunStatus.Cancelled"/>), else null.</summary>
    public string? PartialJson { get; set; }

    /// <summary>The typed failure (set once <see cref="RunStatus.Failed"/>), else null.</summary>
    public RunFailureDetail? Failure { get; set; }

    /// <summary>The run counters (set at any terminal status), else null.</summary>
    public RunStats? Stats { get; set; }

    /// <summary>How long the run waited in the admission queue before it started, in milliseconds (CD-16): set at promotion for
    /// a run that was <see cref="RunStatus.Queued"/>, null for a run started immediately under the cap. Stored here — a plain,
    /// tenant-scoped, queryable document field — so a tenant's <b>p95 queue wait</b> (the pricing-model upgrade signal) is
    /// computable from stored data without a metrics library (docs/PRODUCT.md §Pv.3).</summary>
    public long? QueueWaitMs { get; set; }
}
