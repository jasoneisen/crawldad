using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// <c>GET /runs/{id}/timeline</c> (§13): the run's observability read model — the ordered step list with durations, the
/// redacted input key names, the extracted-value + blob refs, the failure + screenshot ref, the pinned payload revision +
/// script hash, and the backend region. Reads the async <see cref="RunTimeline"/> projection (the lag-tolerant cross-run
/// view, §11), returning the <see cref="RunTimelineResponse"/> DTO — never the internal document. Every field derives from
/// already-scrubbed trace events (§12), so no raw credential or bulk PII can surface here. A run that never started
/// (unknown id) is <c>404</c>.
/// </summary>
public static class RunTimelineEndpoint
{
    /// <summary>Handles <c>GET /runs/{id}/timeline</c>.</summary>
    /// <param name="id">The run to report.</param>
    /// <param name="session">The Marten query session.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns><c>200</c> with the <see cref="RunTimelineResponse"/>, or <c>404</c> when the run is unknown.</returns>
    [WolverineGet("/runs/{id}/timeline")]
    public static async Task<IResult> Handle(Guid id, IQuerySession session, CancellationToken ct)
    {
        var timeline = await session.LoadAsync<RunTimeline>(id, ct);
        return timeline is null ? Results.NotFound() : Results.Ok(ToResponse(timeline));
    }

    /// <summary>Maps the stored timeline projection to its wire DTO.</summary>
    /// <param name="timeline">The stored timeline.</param>
    /// <returns>The wire response.</returns>
    internal static RunTimelineResponse ToResponse(RunTimeline timeline) => new(
        timeline.Id,
        timeline.PayloadName,
        timeline.ScriptHash,
        timeline.PayloadId,
        timeline.PayloadRevision,
        timeline.InputKeys,
        timeline.Region,
        timeline.Status,
        timeline.StartedAt,
        timeline.FinishedAt,
        timeline.DurationMs,
        timeline.Steps,
        timeline.Extracted,
        timeline.Downloads,
        timeline.Screenshots,
        timeline.Failure);
}
