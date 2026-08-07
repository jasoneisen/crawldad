using Crawldad.Contracts.Payloads;

namespace Crawldad.Web.Features.Payloads;

/// <summary>The pinned head of a managed payload (§14.1): the current revision number and the script hash that pins
/// exactly what that revision saved.</summary>
/// <param name="Revision">The head revision — 1 at draft, advanced by one on every subsequent event (revise/rename/archive).</param>
/// <param name="ScriptHash">SHA-256 (lowercase hex) of the head revision's script; unchanged by a rename/archive.</param>
public sealed record PayloadHead(int Revision, string ScriptHash);

/// <summary>
/// The Payload aggregate (§14.1): an anemic snapshot folded from the payload's event stream, whose stream <em>is</em> its
/// version history. A revision is one event = one version, so the head <see cref="PayloadHead.Revision"/> equals the
/// Marten stream version (every event advances it); only a revise changes the <see cref="PayloadHead.ScriptHash"/>. The
/// aggregate carries metadata only — never the script body (that lives in the events, fetched per revision). Decisions
/// live in the endpoints, not here. Exposed through a Contracts DTO via the query endpoints, never directly (§14.1).
/// </summary>
/// <param name="Id">The payload id (the event stream id).</param>
/// <param name="Name">The payload's logical name.</param>
/// <param name="Head">The pinned head (revision + script hash).</param>
/// <param name="Status">The lifecycle state (<see cref="PayloadStatus.Active"/> until archived).</param>
public sealed record Payload(Guid Id, string Name, PayloadHead Head, PayloadStatus Status)
{
    /// <summary>Folds the drafting event into a fresh aggregate (Marten assigns <see cref="Id"/> from the stream).</summary>
    /// <param name="drafted">The drafting event (revision 1).</param>
    public static Payload Create(PayloadDrafted drafted) =>
        new(Guid.Empty, drafted.Name, new PayloadHead(1, drafted.ScriptHash), PayloadStatus.Active);

    /// <summary>Advances the head to the new script revision.</summary>
    /// <param name="revised">The revise event.</param>
    public Payload Apply(PayloadRevised revised) => this with { Head = new PayloadHead(Head.Revision + 1, revised.ScriptHash) };

    /// <summary>Renames the payload; the script hash is unchanged but the revision advances (a metadata version).</summary>
    /// <param name="renamed">The rename event.</param>
    public Payload Apply(PayloadRenamed renamed) => this with { Name = renamed.Name, Head = Head with { Revision = Head.Revision + 1 } };

    /// <summary>Archives the payload; the script hash is unchanged but the revision advances.</summary>
    /// <param name="archived">The archive event.</param>
    public Payload Apply(PayloadArchived archived) => this with { Status = PayloadStatus.Archived, Head = Head with { Revision = Head.Revision + 1 } };
}
