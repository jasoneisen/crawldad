namespace Crawldad.Contracts.Runs;

/// <summary>The tenant's admission-queue snapshot (<c>GET /runs/queue-stats</c>): current queue depth and the p95
/// queue wait — the enqueue→execution-start latency at the 95th percentile across the tenant's promoted runs.</summary>
public sealed record QueueStatsResponse(int Queued, int Sampled, long P95QueueWaitMs);
