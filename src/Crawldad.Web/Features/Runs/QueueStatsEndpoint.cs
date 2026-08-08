using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// <c>GET /runs/queue-stats</c> (CD-16, docs/PRODUCT.md §Pv.3): the tenant's admission-queue observability — current depth and
/// the <b>p95 queue wait</b>, the upgrade signal the slot-pricing model depends on. Both are computed on read, tenant-scoped
/// (CD-1): depth is a count of the tenant's <see cref="QueuedRun"/> rows, and p95 is the 95th percentile (nearest-rank) of the
/// per-run <see cref="RunProgress.QueueWaitMs"/> recorded at promotion — so "p95 queue wait per tenant" is derivable from
/// stored data with no metrics library, exactly as the ticket asks.
/// </summary>
public static class QueueStatsEndpoint
{
    /// <summary>Handles <c>GET /runs/queue-stats</c>.</summary>
    /// <param name="session">The tenant-scoped Marten query session (CD-1).</param>
    /// <param name="ct">Cancels the queries.</param>
    /// <returns><c>200</c> with the tenant's <see cref="QueueStatsResponse"/>.</returns>
    [WolverineGet("/runs/queue-stats")]
    public static async Task<IResult> Handle(IQuerySession session, CancellationToken ct)
    {
        var queued = await session.Query<QueuedRun>().CountAsync(ct);

        // The recorded per-run queue waits (promoted runs only — QueueWaitMs is set at promotion), materialised for the
        // nearest-rank percentile. Bounded by the tenant's run count in the retention window; a heavier deployment would fold
        // this into a projection, but the stored field already makes p95 computable.
        var waits = await session.Query<RunProgress>()
            .Where(progress => progress.QueueWaitMs != null)
            .Select(progress => progress.QueueWaitMs!.Value)
            .ToListAsync(ct);

        return Results.Ok(new QueueStatsResponse(queued, waits.Count, Percentile95(waits)));
    }

    // The 95th percentile by the nearest-rank method: rank = ceil(0.95 * N), the rank-th smallest wait (1-based). An empty
    // sample is 0. Kept here (not a metrics library) because the stored per-run waits are the source of truth.
    private static long Percentile95(IReadOnlyList<long> waits)
    {
        if (waits.Count == 0)
        {
            return 0;
        }

        var ordered = waits.Order().ToArray();
        var rank = (int)Math.Ceiling(0.95 * ordered.Length);
        return ordered[rank - 1];
    }
}
