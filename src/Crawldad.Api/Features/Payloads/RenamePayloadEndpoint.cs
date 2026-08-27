using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Payloads;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Api.Features.Payloads;

/// <summary><c>POST /payloads/{id}/rename</c>: changes a managed payload's logical name (metadata only — script hash
/// unchanged, head revision advances). <c>404</c> when unknown, <c>400</c> when archived or the name is empty. The
/// event's actor is stamped from the authenticated tenant, never the request body.</summary>
public static class RenamePayloadEndpoint
{
    [WolverinePost("/payloads/{id}/rename")]
    public static async Task<IResult> Handle(
        Guid id,
        RenamePayloadRequest request,
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

        var renamed = new PayloadRenamed(scrubber.Scrub(request.Name), clock.GetUtcNow(), tenant.Actor);
        session.Events.Append(id, renamed);
        await session.SaveChangesAsync(ct);

        var head = aggregate.Apply(renamed);
        return Results.Ok(new PayloadResponse(id, head.Name, head.Head.Revision, head.Head.ScriptHash, head.Status));
    }
}
