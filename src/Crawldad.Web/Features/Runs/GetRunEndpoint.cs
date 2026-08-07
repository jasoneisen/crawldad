using System.Text.Json;
using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// <c>GET /runs/{id}</c> (§11): the poll for an async run's state. Reads the executor-owned <see cref="RunProgress"/> read
/// model — <see cref="RunStatus.Running"/> while the executor saga drives it, then the terminal disposition with the
/// scrubbed <c>result</c> / <c>failure</c> / <c>partial</c> and stats. A synchronous run is answered in its own
/// <c>POST /runs</c> response and writes no progress row, so this returns <c>404</c> for one.
/// </summary>
public static class GetRunEndpoint
{
    /// <summary>Handles <c>GET /runs/{id}</c>.</summary>
    /// <param name="id">The run to report.</param>
    /// <param name="session">The Marten query session.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns><c>200</c> with the <see cref="RunStateResponse"/>, or <c>404</c> when there is no such background run.</returns>
    [WolverineGet("/runs/{id}")]
    public static async Task<IResult> Handle(Guid id, IQuerySession session, CancellationToken ct)
    {
        var progress = await session.LoadAsync<RunProgress>(id, ct);
        return progress is null ? Results.NotFound() : Results.Ok(ToResponse(progress));
    }

    /// <summary>Maps the stored progress to the wire state, re-parsing the scrubbed result/partial raw JSON.</summary>
    /// <param name="progress">The stored progress.</param>
    /// <returns>The wire state.</returns>
    internal static RunStateResponse ToResponse(RunProgress progress) => new(
        progress.Id,
        progress.Status,
        Parse(progress.ResultJson),
        progress.Failure,
        Parse(progress.PartialJson),
        progress.Stats);

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
