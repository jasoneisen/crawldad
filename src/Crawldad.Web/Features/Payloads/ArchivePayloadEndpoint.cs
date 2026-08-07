using Crawldad.Contracts.Payloads;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// <c>POST /payloads/{id}/archive</c> (§14.1): archives a managed payload — a terminal lifecycle change that advances the
/// head revision (script hash unchanged) and blocks further revise/rename/archive and new pinned runs. An unknown payload
/// is a <c>404</c>; an already-archived payload is a <c>400</c>. No actor is recorded (§12).
/// </summary>
public static class ArchivePayloadEndpoint
{
    /// <summary>Handles <c>POST /payloads/{id}/archive</c>.</summary>
    /// <param name="id">The payload to archive.</param>
    /// <param name="session">The request-scoped Marten session.</param>
    /// <param name="clock">The time seam for the event timestamp.</param>
    /// <param name="ct">Cancels the save.</param>
    /// <returns><c>200</c> with the archived <see cref="PayloadResponse"/>, <c>404</c> when unknown, or <c>400</c> when already archived.</returns>
    [WolverinePost("/payloads/{id}/archive")]
    public static async Task<IResult> Handle(
        Guid id,
        IDocumentSession session,
        TimeProvider clock,
        CancellationToken ct)
    {
        var aggregate = await session.Events.AggregateStreamAsync<Payload>(id, token: ct);
        if (aggregate is null)
        {
            return Results.NotFound();
        }

        if (aggregate.Status == PayloadStatus.Archived)
        {
            return PayloadProblems.Archived();
        }

        var archived = new PayloadArchived(clock.GetUtcNow());
        session.Events.Append(id, archived);
        await session.SaveChangesAsync(ct);

        var head = aggregate.Apply(archived);
        return Results.Ok(new PayloadResponse(id, head.Name, head.Head.Revision, head.Head.ScriptHash, head.Status));
    }
}
