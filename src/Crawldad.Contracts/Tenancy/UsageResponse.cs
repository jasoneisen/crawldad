namespace Crawldad.Contracts.Tenancy;

/// <summary>Concurrent-run slot occupancy right now: how many slots the tenant holds (<see cref="InUse"/>) against its
/// <see cref="Allowance"/>. A live in-process count from the admission gate — a point-in-time reading, not a stored metric.</summary>
public sealed record UsageSlots(int InUse, int Allowance);

/// <summary>The admission-queue snapshot: the current <see cref="Depth"/> (runs waiting behind the cap) and the p95
/// queue wait across the tenant's promoted runs — the same reading as <c>GET /runs/queue-stats</c>, folded into usage.
/// <see cref="Sampled"/> is the number of recorded per-run waits the p95 is computed over.</summary>
public sealed record UsageQueueStats(int Depth, int Sampled, long P95WaitMs);

/// <summary>Events-per-run against the configured guardrail. <see cref="Guardrail"/> is the server's
/// <c>max-events-per-run</c> cap; <see cref="Avg"/> and <see cref="Max"/> are the mean and peak event count over the most
/// recent <see cref="Sampled"/> terminal runs (a bounded recent window). An approximation surfaced from the runs read
/// model — no dedicated metrics projection — so a tenant can see headroom before a run trips the cap.</summary>
public sealed record UsageEvents(int Guardrail, int Sampled, int Avg, int Max);

/// <summary>The <c>GET /usage</c> response: the tenant's live capacity and consumption against its guardrails — slot
/// occupancy now, queue depth + p95 wait, runs started this calendar month (UTC), and events-per-run over a recent
/// window. Pragmatic and mostly computed-on-read from existing state (the admission gate, the queue, and the runs read
/// model); the counts are honest approximations, documented as such, not a billing ledger.</summary>
public sealed record UsageResponse(
    UsageSlots Slots,
    UsageQueueStats Queue,
    int RunsStartedThisMonth,
    UsageEvents Events);
