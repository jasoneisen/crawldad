using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crawldad.Contracts.Payloads;
using Crawldad.Web.Features.Runs.Interpreter;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// <c>POST /payloads</c> (§12/§14.1, Deliverable 2): validates an inline payload and, if it is sound, drafts it as an
/// event-sourced <see cref="Payload"/> (revision 1) so a malformed payload never becomes executable. Validation runs
/// both passes — (a) the JSON Schema (structure), then (b) the semantic pass (defined-before-use + expression/template
/// parse+arity), the same <see cref="PayloadValidator"/> the run-time pre-pass uses (Deliverable 3). Any failure is a
/// <c>400</c> carrying the full structured error list (path + code + message per error); a valid payload is persisted
/// (own session, <c>StartStream</c> → <c>SaveChanges</c>, mirroring <c>StartRunEndpoint</c>) and echoed as its pinned
/// head. A grossly-shaped body (non-object) is a 400 ProblemDetails via <see cref="SavePayloadRequestValidator"/>.
/// </summary>
public static class DraftPayloadEndpoint
{
    /// <summary>Handles <c>POST /payloads</c>.</summary>
    /// <param name="request">The inline payload to validate and draft.</param>
    /// <param name="session">The request-scoped Marten session.</param>
    /// <param name="clock">The time seam for the event timestamp.</param>
    /// <param name="ct">Cancels the save.</param>
    /// <returns><c>200</c> with the pinned <see cref="PayloadResponse"/>, or <c>400</c> with a <see cref="PayloadValidationProblem"/>.</returns>
    [WolverinePost("/payloads")]
    public static async Task<IResult> Handle(
        SavePayloadRequest request,
        IDocumentSession session,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Pass (a): JSON Schema. Short-circuit so the semantic pass only ever sees a structurally sound document.
        var schemaErrors = PayloadSchema.Validate(request.Payload);
        if (schemaErrors.Count > 0)
        {
            return Results.BadRequest(new PayloadValidationProblem(schemaErrors));
        }

        // Pass (b): the shared semantic pass (defined-before-use + expression/template/path parse+arity).
        var semanticErrors = PayloadValidator.Validate(request.Payload);
        if (semanticErrors.Count > 0)
        {
            return Results.BadRequest(new PayloadValidationProblem([.. semanticErrors.Select(ToError)]));
        }

        // Valid → draft revision 1. scriptHash uses the same convention as RunStarted (SHA-256 of the payload bytes).
        var payloadId = Guid.NewGuid();
        var name = request.Payload.GetProperty("name").GetString()!;
        var script = request.Payload.GetRawText();
        var scriptHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script)));

        session.Events.StartStream<Payload>(payloadId, new PayloadDrafted(name, script, scriptHash, clock.GetUtcNow()));
        await session.SaveChangesAsync(ct);

        return Results.Ok(new PayloadResponse(payloadId, name, 1, scriptHash, PayloadStatus.Active));
    }

    private static PayloadValidationError ToError(PayloadIssue issue) => new(issue.Path, issue.Code, issue.Message);
}
