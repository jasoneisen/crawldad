using Crawldad.Api.Features.Payloads;
using Crawldad.Contracts.Runs;
using Marten;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Api.Features.Runs;

/// <summary><c>GET /runs/{id}/drift</c>: reports a run's pinned payload revision against the payload's current head, so a
/// historical run that pinned an older revision is flagged as drifted once the payload moves on. Both revisions' script
/// hashes are reported (equal hashes under a mismatch = a metadata-only head move). An inline run never drifts.</summary>
public static class RunDriftEndpoint
{
    /// <summary>Handles <c>GET /runs/{id}/drift</c>.</summary>
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
