using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Payloads;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Api.Features.Payloads;

/// <summary><c>POST /payloads/{id}/archive</c>: a terminal lifecycle change that advances the head revision (script hash
/// unchanged) and blocks further revise/rename/archive and new pinned runs. <c>404</c> when unknown, <c>400</c> when
/// already archived. The event's actor is stamped from the authenticated tenant, never the request body.</summary>
public static class ArchivePayloadEndpoint
{
    [WolverinePost("/payloads/{id}/archive")]
    public static async Task<IResult> Handle(
        Guid id,
        IDocumentSession session,
        // [FromServices]: this POST endpoint has no request body, so without the marker Wolverine would treat the first
        // complex parameter (the tenant context) as the body to deserialize. The marker keeps it a resolved service.
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

        var archived = new PayloadArchived(clock.GetUtcNow(), tenant.Actor);
        session.Events.Append(id, archived);
        await session.SaveChangesAsync(ct);

        var head = aggregate.Apply(archived);
        return Results.Ok(new PayloadResponse(id, head.Name, head.Head.Revision, head.Head.ScriptHash, head.Status));
    }
}
