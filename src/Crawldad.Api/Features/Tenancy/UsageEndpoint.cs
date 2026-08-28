using Crawldad.Api.Features.Runs;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Wolverine.Http;

namespace Crawldad.Api.Features.Tenancy;

/// <summary><c>GET /usage</c>: the tenant's live capacity and consumption against its guardrails. Pragmatic and
/// computed-on-read from existing state — the admission gate (slot occupancy now), the durable queue + the recorded
/// per-run queue waits (depth + p95, the same reading as <c>GET /runs/queue-stats</c>), and the runs listing read model
/// (runs started this month; events-per-run over a recent window). The counts are honest approximations, not a billing
/// ledger: slot occupancy is a per-process point-in-time count, and the event-window avg/max is a bounded recent sample.</summary>
public static class UsageEndpoint
{
    /// <summary>The number of most-recent terminal runs the events-per-run avg/max is sampled over.</summary>
    public const int RecentEventWindow = 100;

    /// <summary>Handles <c>GET /usage</c>.</summary>
    [WolverineGet("/usage")]
    public static async Task<IResult> Handle(
        TenantContext tenant,
        IRunAdmissionGate gate,
        IQuerySession session,
        ITenantRegistryStore registry,
        IOptions<TenantOptions> tenants,
        IOptions<RunLimitsOptions> limits,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Slot allowance resolves registry-first, env-fallback (see GET /tenant): a registry tenant is absent from env
        // config, so the old .First() over env options threw for it. Every other number below comes from tenant-scoped
        // sources (the gate keyed by tenant id, and the request's tenant-scoped Marten session), so they are already
        // correct for either tenant kind.
        var registered = await registry.FindAsync(tenant.TenantId, ct);
        var descriptor = tenants.Value.Tenants.FirstOrDefault(t => string.Equals(t.Id, tenant.TenantId, StringComparison.Ordinal));
        var slotAllowance = TenantProfileResolution.SlotAllowance(registered, descriptor, limits.Value);
        var slots = new UsageSlots(gate.ActiveCount(tenant.TenantId), slotAllowance);

        // The queue snapshot: depth is the count of QueuedRun rows; p95 is the nearest-rank percentile of the recorded
        // per-run queue waits — the same machinery GET /runs/queue-stats uses (reused, not re-derived).
        var depth = await session.Query<QueuedRun>().CountAsync(ct);
        var waits = await session.Query<RunProgress>()
            .Where(progress => progress.QueueWaitMs != null)
            .Select(progress => progress.QueueWaitMs!.Value)
            .ToListAsync(ct);
        var queue = new UsageQueueStats(depth, waits.Count, QueueStatsEndpoint.Percentile95(waits));

        // Runs started this calendar month (UTC), from the listing read model's StartedAt.
        var now = clock.GetUtcNow();
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var runsThisMonth = await session.Query<RunSummary>().CountAsync(summary => summary.StartedAt >= monthStart, ct);

        // Events-per-run over the most recent terminal runs (EventCount is set only at terminal). A bounded window read
        // from the same summary rows — an approximation surfaced for headroom, not a full-history projection.
        var recent = await session.Query<RunSummary>()
            .Where(summary => summary.EventCount != null)
            .OrderByDescending(summary => summary.StartedAt).ThenByDescending(summary => summary.Id)
            .Take(RecentEventWindow)
            .ToListAsync(ct);
        var counts = recent.Select(summary => summary.EventCount!.Value).ToList();
        var events = new UsageEvents(
            limits.Value.MaxEventsPerRun,
            counts.Count,
            counts.Count > 0 ? (int)Math.Round(counts.Average()) : 0,
            counts.Count > 0 ? counts.Max() : 0);

        return Results.Ok(new UsageResponse(slots, queue, runsThisMonth, events));
    }
}
