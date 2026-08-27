using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Api.Features.Runs;

/// <summary><c>GET /runs/queue-stats</c>: the tenant's admission-queue observability — current depth and the p95 queue
/// wait. Both computed on read: depth is a count of <see cref="QueuedRun"/> rows, and p95 is the nearest-rank percentile
/// of the per-run <see cref="RunProgress.QueueWaitMs"/> recorded at promotion.</summary>
public static class QueueStatsEndpoint
{
    /// <summary>Handles <c>GET /runs/queue-stats</c>.</summary>
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
