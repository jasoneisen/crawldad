using Crawldad.Contracts.Payloads;
using Crawldad.Web.Infrastructure.Security;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// <c>POST /payloads/{id}/rename</c> (§14.1): changes a managed payload's logical name (metadata only — the script hash is
/// unchanged, but the head revision advances). An unknown payload is a <c>404</c>; an archived payload is a <c>400</c>; an
/// empty name is a <c>400</c> ProblemDetails via <see cref="RenamePayloadRequestValidator"/>. No actor is recorded (§12).
/// </summary>
public static class RenamePayloadEndpoint
{
    /// <summary>Handles <c>POST /payloads/{id}/rename</c>.</summary>
    /// <param name="id">The payload to rename.</param>
    /// <param name="request">The new name.</param>
    /// <param name="session">The request-scoped Marten session.</param>
    /// <param name="scrubber">Redacts credential material from the persisted name (§12).</param>
    /// <param name="clock">The time seam for the event timestamp.</param>
    /// <param name="ct">Cancels the save.</param>
    /// <returns><c>200</c> with the new head <see cref="PayloadResponse"/>, <c>404</c> when unknown, or <c>400</c> when archived.</returns>
    [WolverinePost("/payloads/{id}/rename")]
    public static async Task<IResult> Handle(
        Guid id,
        RenamePayloadRequest request,
        IDocumentSession session,
        CredentialScrubber scrubber,
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

        var renamed = new PayloadRenamed(scrubber.Scrub(request.Name), clock.GetUtcNow());
        session.Events.Append(id, renamed);
        await session.SaveChangesAsync(ct);

        var head = aggregate.Apply(renamed);
        return Results.Ok(new PayloadResponse(id, head.Name, head.Head.Revision, head.Head.ScriptHash, head.Status));
    }
}
