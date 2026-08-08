namespace Crawldad.Contracts.Runs;

/// <summary>
/// The tenant's admission-queue observability snapshot (CD-16, docs/PRODUCT.md §Pv.3): <c>GET /runs/queue-stats</c>. Surfaces
/// the current queue depth and the <b>p95 queue wait</b> — the enqueue→execution-start latency at the 95th percentile across
/// the tenant's promoted runs — computed on read from stored data (the per-run <c>queueWaitMs</c>), no metrics library. This
/// is the upgrade signal the pricing model leans on: sustained queue wait means "add slots" (the dashboard says
/// "p95 queue wait this week: 4 m 12 s — add 5 slots?").
/// </summary>
/// <param name="Queued">The tenant's current queue depth — runs admitted at the cap and waiting for a slot right now.</param>
/// <param name="Sampled">The number of promoted runs whose queue wait was recorded (the p95 sample size).</param>
/// <param name="P95QueueWaitMs">The 95th-percentile queue wait across those runs, in milliseconds (0 when none have been sampled).</param>
public sealed record QueueStatsResponse(int Queued, int Sampled, long P95QueueWaitMs);
