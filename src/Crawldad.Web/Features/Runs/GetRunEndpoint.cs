using System.Text.Json;
using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary><c>GET /runs/{id}</c>: the poll for an async run's state. Reports <c>queued</c> with a live 1-based queue
/// <c>position</c> while it waits behind the cap, <c>running</c> while executing, then the terminal disposition with the
/// scrubbed result/failure/partial and stats. A synchronous run writes no progress row, so this 404s for one.</summary>
public static class GetRunEndpoint
{
    /// <summary>Handles <c>GET /runs/{id}</c>.</summary>
    [WolverineGet("/runs/{id}")]
    public static async Task<IResult> Handle(Guid id, IQuerySession session, RunQueue queue, CancellationToken ct)
    {
        var progress = await session.LoadAsync<RunProgress>(id, ct);
        if (progress is null)
        {
            return Results.NotFound();
        }

        var position = progress.Status == RunStatus.Queued ? await queue.PositionAsync(session, id, ct) : null;
        return Results.Ok(ToResponse(progress, position));
    }

    /// <summary>Maps the stored progress to the wire state, re-parsing the scrubbed result/partial raw JSON and attaching
    /// the live queue position, the recorded queue wait, and the result-expiry marker (set once retention aged the body out).</summary>
    internal static RunStateResponse ToResponse(RunProgress progress, int? position = null) => new(
        progress.Id,
        progress.Status,
        Parse(progress.ResultJson),
        progress.Failure,
        Parse(progress.PartialJson),
        progress.Stats,
        Position: position,
        QueueWaitMs: progress.QueueWaitMs,
        ResultExpiredAt: progress.ResultExpiredAt);

    private static JsonElement? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
