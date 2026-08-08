using System.Text.Json;
using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// <c>GET /runs/{id}</c> (§11/CD-16): the poll for an async run's state. Reads the executor-owned <see cref="RunProgress"/>
/// read model — <see cref="RunStatus.Queued"/> with a live 1-based queue <c>position</c> while it waits behind the cap,
/// <see cref="RunStatus.Running"/> while the executor saga drives it, then the terminal disposition with the scrubbed
/// <c>result</c> / <c>failure</c> / <c>partial</c> and stats (plus <c>queueWaitMs</c> for a run that queued). A synchronous run
/// is answered in its own <c>POST /runs</c> response and writes no progress row, so this returns <c>404</c> for one.
/// </summary>
public static class GetRunEndpoint
{
    /// <summary>Handles <c>GET /runs/{id}</c>.</summary>
    /// <param name="id">The run to report.</param>
    /// <param name="session">The Marten query session.</param>
    /// <param name="queue">The run queue — resolves the 1-based position of a queued run (CD-16), computed on read.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns><c>200</c> with the <see cref="RunStateResponse"/>, or <c>404</c> when there is no such background run.</returns>
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

    /// <summary>Maps the stored progress to the wire state, re-parsing the scrubbed result/partial raw JSON and attaching the
    /// live queue position (queued runs, CD-16) and the recorded queue wait.</summary>
    /// <param name="progress">The stored progress.</param>
    /// <param name="position">The run's 1-based queue position when queued, else null.</param>
    /// <returns>The wire state.</returns>
    internal static RunStateResponse ToResponse(RunProgress progress, int? position = null) => new(
        progress.Id,
        progress.Status,
        Parse(progress.ResultJson),
        progress.Failure,
        Parse(progress.PartialJson),
        progress.Stats,
        Position: position,
        QueueWaitMs: progress.QueueWaitMs);

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
