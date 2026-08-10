using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary><c>GET /runs/{id}/timeline</c>: the run's observability read model — the ordered step list with durations,
/// input key names, extracted/blob refs, failure + screenshot ref, pinned payload revision, and backend region. Reads
/// the async <see cref="RunTimeline"/> projection, returning <see cref="RunTimelineResponse"/> — never the internal document.</summary>
public static class RunTimelineEndpoint
{
    /// <summary>Handles <c>GET /runs/{id}/timeline</c>.</summary>
    [WolverineGet("/runs/{id}/timeline")]
    public static async Task<IResult> Handle(Guid id, IQuerySession session, CancellationToken ct)
    {
        var timeline = await session.LoadAsync<RunTimeline>(id, ct);
        return timeline is null ? Results.NotFound() : Results.Ok(ToResponse(timeline));
    }

    /// <summary>Maps the stored timeline projection to its wire DTO.</summary>
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
