namespace Crawldad.Web.Features.Runs;

/// <summary>
/// One entry in a tenant's durable FIFO admission queue (CD-16, docs/PRODUCT.md §Pv.3): a run that was accepted at the
/// tenant's concurrent-run cap and is waiting for a slot. It is a plain, tenant-scoped Marten document (conjoined tenancy,
/// CD-1) — so the queue <b>survives process restarts</b> — that carries the deferred run definition verbatim: exactly the
/// fields <see cref="StartRun"/> needs, so at promotion the run is kicked through the unchanged async executor path without
/// the originating request. It is deleted the instant the run is promoted (<see cref="RunDequeued"/>), cancelled while queued,
/// or expires its queue wait — so the set of these documents for a tenant <em>is</em> its live queue.
/// <para>
/// Ordering is by <see cref="Sequence"/>, a process-monotonic counter assigned at enqueue (not a timestamp — the test clock is
/// frozen, and two enqueues can share a wall-clock instant under load), seeded on restart from the max surviving value so FIFO
/// is preserved across a restart. The run's observable state lives in its <see cref="RunProgress"/> row (<c>queued</c> until
/// promotion) and its event stream (opened with <see cref="RunQueued"/>); this document holds only what promotion needs.
/// </para>
/// </summary>
public sealed class QueuedRun
{
    /// <summary>The run id (the document id, and the run's event-stream id).</summary>
    public Guid Id { get; set; }

    /// <summary>The FIFO ordering key: a process-monotonic sequence assigned at enqueue, seeded across restarts from the max
    /// surviving value. The tenant's oldest queued run is the one with the smallest sequence; queue <c>position</c> is a count
    /// of the tenant's queued runs with a smaller sequence, computed on read (never a denormalised counter that can drift).</summary>
    public long Sequence { get; set; }

    /// <summary>The payload's logical name (carried onto <see cref="StartRun"/> at promotion).</summary>
    public string PayloadName { get; set; } = "";

    /// <summary>The executed script's hash (drift/audit; carried onto <see cref="StartRun"/>).</summary>
    public string ScriptHash { get; set; } = "";

    /// <summary>The payload document JSON — already credential-scrubbed and executable, exactly as <see cref="StartRun.Script"/>.</summary>
    public string Script { get; set; } = "";

    /// <summary>The run inputs JSON — credentials are by-reference only, so this is safe to persist (as <see cref="StartRun.Inputs"/>).</summary>
    public string Inputs { get; set; } = "";

    /// <summary>The pinned managed payload (§14.2), or null for an inline run.</summary>
    public Guid? PayloadId { get; set; }

    /// <summary>The pinned revision (§14.2), or null for an inline run.</summary>
    public int? PayloadRevision { get; set; }

    /// <summary>The run's wall-clock cap in milliseconds (§8.4) — scheduled as the saga deadline <b>at promotion</b>, so time
    /// spent queued never counts against it (CD-16).</summary>
    public int DeadlineMs { get; set; }

    /// <summary>When the run was enqueued (through the <see cref="TimeProvider"/> seam) — the start of its queue wait, subtracted
    /// from the promotion instant to record the realised wait (the p95 upgrade signal).</summary>
    public DateTimeOffset QueuedAt { get; set; }
}
