using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Payloads;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// <c>GET /runs/{id}/drift</c> (§14.1/§13): reports a run's pinned payload revision against the payload's current head, so
/// a historical run that pinned an older revision is flagged as drifted once the payload moves on. Drift = the pinned
/// revision is no longer the head revision (§14.1). Both revisions' script hashes are reported for diagnosis (equal hashes
/// under a revision mismatch = a metadata-only head move). An inline run has no pinned payload and never drifts.
/// </summary>
public static class RunDriftEndpoint
{
    /// <summary>Handles <c>GET /runs/{id}/drift</c>.</summary>
    /// <param name="id">The run to report drift for.</param>
    /// <param name="session">The Marten session.</param>
    /// <param name="ct">Cancels the query.</param>
    /// <returns><c>200</c> with the <see cref="RunDriftResponse"/>, or <c>404</c> when the run is unknown.</returns>
    [WolverineGet("/runs/{id}/drift")]
    public static async Task<IResult> Handle(Guid id, IDocumentSession session, CancellationToken ct)
    {
        var run = await session.Events.AggregateStreamAsync<Run>(id, token: ct);
        if (run is null)
        {
            return Results.NotFound();
        }

        if (run.PayloadId is not Guid payloadId)
        {
            // Inline run: no pinned payload, so it can never drift (head fields null).
            return Results.Ok(new RunDriftResponse(id, null, null, run.ScriptHash, null, null, false));
        }

        // A pinned run always references an existing payload (payloads are archived, never deleted) with a set revision.
        var payload = await session.Events.AggregateStreamAsync<Payload>(payloadId, token: ct);
        var pinnedRevision = run.PayloadRevision!.Value;
        var headRevision = payload!.Head.Revision;
        var drifted = pinnedRevision != headRevision;
        return Results.Ok(new RunDriftResponse(id, payloadId, pinnedRevision, run.ScriptHash, headRevision, payload.Head.ScriptHash, drifted));
    }
}
