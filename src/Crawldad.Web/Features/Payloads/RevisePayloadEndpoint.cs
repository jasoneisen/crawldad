using System.Text.Json;
using Crawldad.Contracts.Payloads;
using Crawldad.Web.Infrastructure.Security;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Web.Features.Payloads;

/// <summary><c>POST /payloads/{id}/revise</c>: appends a new script revision to a managed payload, running the SAME
/// scrub-then-validate gate as a draft (<see cref="PayloadScript"/> + <see cref="PayloadValidation"/>) so every persisted
/// revision is executable and credential-free. The event's actor is stamped from the tenant, never the request body.</summary>
public static class RevisePayloadEndpoint
{
    [WolverinePost("/payloads/{id}/revise")]
    public static async Task<IResult> Handle(
        Guid id,
        RevisePayloadRequest request,
        IDocumentSession session,
        CredentialScrubber scrubber,
        [FromServices] TenantContext tenant,
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
        var revised = new PayloadRevised(scrubbed.Script, scrubbed.ScriptHash, note, clock.GetUtcNow(), tenant.Actor);
        session.Events.Append(id, revised);
        await session.SaveChangesAsync(ct);

        // Fold the appended event in-memory (the aggregate's own logic) to shape the new head — no reload needed.
        var head = aggregate.Apply(revised);
        return Results.Ok(new PayloadResponse(id, head.Name, head.Head.Revision, head.Head.ScriptHash, head.Status));
    }
}
