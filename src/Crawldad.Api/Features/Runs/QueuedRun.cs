namespace Crawldad.Api.Features.Runs;

/// <summary>One entry in a tenant's durable FIFO admission queue: a run accepted at the concurrent-run cap and waiting for
/// a slot, carrying the deferred run definition so promotion kicks it through the unchanged executor path. Ordered by
/// <see cref="Sequence"/>, a process-monotonic counter seeded from the surviving max on restart so FIFO holds across restarts; deleted on promotion, cancel, or wait expiry.</summary>
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

    /// <summary>The pinned managed payload, or null for an inline run.</summary>
    public Guid? PayloadId { get; set; }

    /// <summary>The pinned revision, or null for an inline run.</summary>
    public int? PayloadRevision { get; set; }

    /// <summary>The run's wall-clock cap in milliseconds — scheduled as the saga deadline at promotion, so time spent
    /// queued never counts against it.</summary>
    public int DeadlineMs { get; set; }

    /// <summary>When the run was enqueued (through the <see cref="TimeProvider"/> seam) — the start of its queue wait, subtracted
    /// from the promotion instant to record the realised wait (the p95 upgrade signal).</summary>
    public DateTimeOffset QueuedAt { get; set; }
}
