using System.Text.Json;
using Crawldad.Contracts.Payloads;
using Crawldad.Web.Infrastructure.Security;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// <c>POST /payloads</c> (§12/§14.1, Deliverable 2): scrubs, validates, and drafts an inline payload as revision 1 of an
/// event-sourced <see cref="Payload"/> — so a malformed or credential-bearing payload never becomes executable. The
/// script is first scrubbed at the persistence boundary (<see cref="PayloadScript"/>, §12), then the <em>scrubbed</em>
/// artifact is validated (JSON Schema + semantic pass via <see cref="PayloadValidation"/>, Deliverable 3), name-extracted,
/// and persisted — one and the same bytes are validated, hashed, stored, and later re-executed. Any validation failure is
/// a <c>400</c> carrying the full structured error list; a grossly-shaped body (non-object) is a 400 ProblemDetails via
/// <see cref="SavePayloadRequestValidator"/>. On actor/<c>by</c>: none is recorded — identity comes from the authenticated
/// principal, never the request body (§12), and is post-MVP (mirrors <c>RunStarted</c>).
/// </summary>
public static class DraftPayloadEndpoint
{
    /// <summary>Handles <c>POST /payloads</c>.</summary>
    /// <param name="request">The inline payload to validate and draft.</param>
    /// <param name="session">The request-scoped Marten session.</param>
    /// <param name="scrubber">Redacts credential material from the persisted script and name (§12).</param>
    /// <param name="clock">The time seam for the event timestamp.</param>
    /// <param name="ct">Cancels the save.</param>
    /// <returns><c>200</c> with the pinned <see cref="PayloadResponse"/>, or <c>400</c> with a <see cref="PayloadValidationProblem"/>.</returns>
    [WolverinePost("/payloads")]
    public static async Task<IResult> Handle(
        SavePayloadRequest request,
        IDocumentSession session,
        CredentialScrubber scrubber,
        TimeProvider clock,
        CancellationToken ct)
    {
        // §12: scrub first, then validate/name/hash/store exactly the scrubbed bytes (see PayloadScript for the decision).
        var scrubbed = PayloadScript.Scrub(request.Payload, scrubber);
        using var document = JsonDocument.Parse(scrubbed.Script);
        var payload = document.RootElement;

        var problem = PayloadValidation.Validate(payload);
        if (problem is not null)
        {
            return Results.BadRequest(problem);
        }

        var payloadId = Guid.NewGuid();
        var name = payload.GetProperty("name").GetString()!; // from the scrubbed document, so already redacted
        session.Events.StartStream<Payload>(payloadId, new PayloadDrafted(name, scrubbed.Script, scrubbed.ScriptHash, clock.GetUtcNow()));
        await session.SaveChangesAsync(ct);

        return Results.Ok(new PayloadResponse(payloadId, name, 1, scrubbed.ScriptHash, PayloadStatus.Active));
    }
}
