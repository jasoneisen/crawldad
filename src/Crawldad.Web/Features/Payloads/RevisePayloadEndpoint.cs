using System.Text.Json;
using Crawldad.Contracts.Payloads;
using Crawldad.Web.Infrastructure.Security;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// <c>POST /payloads/{id}/revise</c> (§14.1): appends a new script revision to a managed payload. The revised script runs
/// the SAME scrub-then-validate gate as a draft (<see cref="PayloadScript"/> + <see cref="PayloadValidation"/>), so every
/// persisted revision is executable and credential-free (§12). An unknown payload is a <c>404</c>; an archived payload is
/// a <c>400</c> (cannot revise); an invalid script is a <c>400</c> with the structured error list. No actor is recorded
/// (identity is post-MVP, from the principal not the body — §12).
/// </summary>
public static class RevisePayloadEndpoint
{
    /// <summary>Handles <c>POST /payloads/{id}/revise</c>.</summary>
    /// <param name="id">The payload to revise.</param>
    /// <param name="request">The revised payload + optional note.</param>
    /// <param name="session">The request-scoped Marten session.</param>
    /// <param name="scrubber">Redacts credential material from the persisted script and note (§12).</param>
    /// <param name="clock">The time seam for the event timestamp.</param>
    /// <param name="ct">Cancels the save.</param>
    /// <returns><c>200</c> with the new head <see cref="PayloadResponse"/>, <c>404</c> when unknown, or <c>400</c> when archived/invalid.</returns>
    [WolverinePost("/payloads/{id}/revise")]
    public static async Task<IResult> Handle(
        Guid id,
        RevisePayloadRequest request,
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

        var scrubbed = PayloadScript.Scrub(request.Payload, scrubber);
        using var document = JsonDocument.Parse(scrubbed.Script);
        var problem = PayloadValidation.Validate(document.RootElement);
        if (problem is not null)
        {
            return Results.BadRequest(problem);
        }

        var note = request.Note is null ? null : scrubber.Scrub(request.Note);
        var revised = new PayloadRevised(scrubbed.Script, scrubbed.ScriptHash, note, clock.GetUtcNow());
        session.Events.Append(id, revised);
        await session.SaveChangesAsync(ct);

        // Fold the appended event in-memory (the aggregate's own logic) to shape the new head — no reload needed.
        var head = aggregate.Apply(revised);
        return Results.Ok(new PayloadResponse(id, head.Name, head.Head.Revision, head.Head.ScriptHash, head.Status));
    }
}
