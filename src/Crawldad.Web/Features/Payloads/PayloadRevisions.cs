using Crawldad.Contracts.Payloads;
using Marten;

namespace Crawldad.Web.Features.Payloads;

/// <summary>One resolved revision of a payload: the stored (scrubbed) script and its hash.</summary>
/// <param name="Script">The revision's payload document (scrubbed, executable).</param>
/// <param name="ScriptHash">The revision's script hash (SHA-256, lowercase hex).</param>
internal sealed record ResolvedRevision(string Script, string ScriptHash);

/// <summary>A payload folded from its event stream, exposing every revision's script. Revision <c>N</c> is the state
/// after the first <c>N</c> events (index <c>N-1</c> in <see cref="Revisions"/>); a rename/archive carries the prior
/// script forward unchanged.</summary>
internal sealed record ResolvedPayload(PayloadStatus Status, IReadOnlyList<ResolvedRevision> Revisions)
{
    /// <summary>The current head revision (the number of events in the stream).</summary>
    public int HeadRevision => Revisions.Count;

    /// <summary>The script at a given revision, or null when the revision is out of range.</summary>
    /// <param name="revision">The 1-based revision.</param>
    public ResolvedRevision? At(int revision) => revision >= 1 && revision <= Revisions.Count ? Revisions[revision - 1] : null;
}

/// <summary>Resolves a managed payload's revisions from its event stream — the read path for run-pinning
/// (<c>StartRunEndpoint</c>) and the revision/diff query endpoints, all of which need the script body. Folds the
/// stream once and records each version's script so any revision can be pinned or diffed.</summary>
internal static class PayloadRevisions
{
    /// <summary>Loads a payload's folded revision history, or null when the payload does not exist.</summary>
    public static async Task<ResolvedPayload?> LoadAsync(IDocumentSession session, Guid id, CancellationToken ct)
    {
        var events = await session.Events.FetchStreamAsync(id, token: ct);
        if (events.Count == 0)
        {
            return null;
        }

        var script = "";
        var hash = "";
        var status = PayloadStatus.Active;
        var revisions = new List<ResolvedRevision>(events.Count);
        foreach (var e in events)
        {
            // The payload stream carries exactly these four event types; a draft/revise sets a new script, and a
            // rename (the default arm) carries the prior script forward unchanged.
            switch (e.Data)
            {
                case PayloadDrafted drafted:
                    script = drafted.Script;
                    hash = drafted.ScriptHash;
                    break;
                case PayloadRevised revised:
                    script = revised.Script;
                    hash = revised.ScriptHash;
                    break;
                case PayloadArchived:
                    status = PayloadStatus.Archived;
                    break;
                default: // PayloadRenamed — metadata only; the script/hash carry forward
                    break;
            }

            revisions.Add(new ResolvedRevision(script, hash));
        }

        return new ResolvedPayload(status, revisions);
    }
}
