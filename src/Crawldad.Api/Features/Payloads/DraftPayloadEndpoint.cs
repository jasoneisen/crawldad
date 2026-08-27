using System.Text.Json;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Payloads;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Api.Features.Payloads;

/// <summary><c>POST /payloads</c>: scrubs, validates, and drafts an inline payload as revision 1 of an event-sourced
/// <see cref="Payload"/>. The script is scrubbed first, then the <em>same scrubbed bytes</em> are validated, hashed,
/// stored, and later re-executed. The event's actor is stamped from the authenticated principal, never the request body.</summary>
public static class DraftPayloadEndpoint
{
    [WolverinePost("/payloads")]
    public static async Task<IResult> Handle(
        SavePayloadRequest request,
        IDocumentSession session,
        CredentialScrubber scrubber,
        [FromServices] TenantContext tenant,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Scrub first, then validate/name/hash/store exactly the scrubbed bytes (see PayloadScript for the rationale).
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
        session.Events.StartStream<Payload>(payloadId, new PayloadDrafted(name, scrubbed.Script, scrubbed.ScriptHash, clock.GetUtcNow(), tenant.Actor));
        await session.SaveChangesAsync(ct);

        return Results.Ok(new PayloadResponse(payloadId, name, 1, scrubbed.ScriptHash, PayloadStatus.Active));
    }
}
