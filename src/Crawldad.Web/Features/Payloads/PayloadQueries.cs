using System.Text.Json;
using Crawldad.Contracts.Payloads;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// The managed-payload read side (§14.1): list, get-state, get-a-revision, and diff. Every endpoint returns a Contracts
/// DTO, never the internal aggregate. Listing reads the async <c>PayloadSummary</c> projection (cross-payload, lag
/// tolerated); get-state reads the aggregate from its own event stream (read-your-writes, §11); revision and diff fold the
/// stream for the script body (the event-sourced equivalent of <c>AggregateStreamAsync(id, version:N)</c>). Every returned
/// script is the stored, credential-scrubbed document (§12).
/// </summary>
public static class PayloadQueries
{
    /// <summary><c>GET /payloads</c> — every managed payload's summary row, from the listing read model.</summary>
    /// <param name="session">The Marten session.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns>The payload list.</returns>
    [WolverineGet("/payloads")]
    public static async Task<PayloadListResponse> List(IDocumentSession session, CancellationToken ct)
    {
        var summaries = await session.Query<PayloadSummary>().ToListAsync(ct);
        return new PayloadListResponse([.. summaries.Select(ToItem)]);
    }

    /// <summary><c>GET /payloads/{id}</c> — the payload's current state DTO (read-your-writes from its stream).</summary>
    /// <param name="id">The payload id.</param>
    /// <param name="session">The Marten session.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns><c>200</c> with the <see cref="PayloadResponse"/>, or <c>404</c> when unknown.</returns>
    [WolverineGet("/payloads/{id}")]
    public static async Task<IResult> Get(Guid id, IDocumentSession session, CancellationToken ct)
    {
        var payload = await session.Events.AggregateStreamAsync<Payload>(id, token: ct);
        return payload is null
            ? Results.NotFound()
            : Results.Ok(new PayloadResponse(id, payload.Name, payload.Head.Revision, payload.Head.ScriptHash, payload.Status));
    }

    /// <summary><c>GET /payloads/{id}/revisions/{revision}</c> — one historical revision's script + metadata.</summary>
    /// <param name="id">The payload id.</param>
    /// <param name="revision">The 1-based revision.</param>
    /// <param name="session">The Marten session.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns><c>200</c> with the <see cref="PayloadRevisionResponse"/>, or <c>404</c> when the payload/revision is unknown.</returns>
    [WolverineGet("/payloads/{id}/revisions/{revision}")]
    public static async Task<IResult> Revision(Guid id, int revision, IDocumentSession session, CancellationToken ct)
    {
        var resolved = await PayloadRevisions.LoadAsync(session, id, ct);
        var at = resolved?.At(revision);
        if (at is null)
        {
            return Results.NotFound();
        }

        using var document = JsonDocument.Parse(at.Script);
        return Results.Ok(new PayloadRevisionResponse(id, revision, at.ScriptHash, document.RootElement.Clone()));
    }

    /// <summary><c>GET /payloads/{id}/diff/{from}/{to}</c> — both revisions' scripts plus a minimal structural diff.</summary>
    /// <param name="id">The payload id.</param>
    /// <param name="from">The base revision.</param>
    /// <param name="to">The compared revision.</param>
    /// <param name="session">The Marten session.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns><c>200</c> with the <see cref="PayloadDiffResponse"/>, or <c>404</c> when the payload/either revision is unknown.</returns>
    [WolverineGet("/payloads/{id}/diff/{from}/{to}")]
    public static async Task<IResult> Diff(Guid id, int from, int to, IDocumentSession session, CancellationToken ct)
    {
        var resolved = await PayloadRevisions.LoadAsync(session, id, ct);
        var fromRevision = resolved?.At(from);
        var toRevision = resolved?.At(to);
        if (fromRevision is null || toRevision is null)
        {
            return Results.NotFound();
        }

        using var fromDocument = JsonDocument.Parse(fromRevision.Script);
        using var toDocument = JsonDocument.Parse(toRevision.Script);
        var changes = PayloadDiff.Compute(fromDocument.RootElement, toDocument.RootElement);
        return Results.Ok(new PayloadDiffResponse(id, from, to, fromDocument.RootElement.Clone(), toDocument.RootElement.Clone(), changes));
    }

    private static PayloadListItem ToItem(PayloadSummary summary) =>
        new(summary.Id, summary.Name, summary.Revision, summary.ScriptHash, summary.Status, summary.DraftedAt, summary.UpdatedAt);
}
