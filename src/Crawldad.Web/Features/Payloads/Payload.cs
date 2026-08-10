using Crawldad.Contracts.Payloads;

namespace Crawldad.Web.Features.Payloads;

/// <summary>The pinned head of a managed payload: the current revision number and the script hash it pins.</summary>
/// <param name="Revision">The head revision — 1 at draft, advanced by one on every subsequent event (revise/rename/archive).</param>
/// <param name="ScriptHash">SHA-256 (lowercase hex) of the head revision's script; unchanged by a rename/archive.</param>
public sealed record PayloadHead(int Revision, string ScriptHash);

/// <summary>The Payload aggregate: an anemic snapshot folded from the payload's event stream, whose stream <em>is</em> its
/// version history — the head <see cref="PayloadHead.Revision"/> equals the Marten stream version. The aggregate carries
/// metadata only, never the script body (that lives in the events, fetched per revision).</summary>
public sealed record Payload(Guid Id, string Name, PayloadHead Head, PayloadStatus Status)
{
    /// <summary>Folds the drafting event into a fresh aggregate (Marten assigns <see cref="Id"/> from the stream).</summary>
    public static Payload Create(PayloadDrafted drafted) =>
        new(Guid.Empty, drafted.Name, new PayloadHead(1, drafted.ScriptHash), PayloadStatus.Active);

    /// <summary>Advances the head to the new script revision.</summary>
    public Payload Apply(PayloadRevised revised) => this with { Head = new PayloadHead(Head.Revision + 1, revised.ScriptHash) };

    /// <summary>Renames the payload; the script hash is unchanged but the revision advances (a metadata version).</summary>
    public Payload Apply(PayloadRenamed renamed) => this with { Name = renamed.Name, Head = Head with { Revision = Head.Revision + 1 } };

    /// <summary>Archives the payload; the script hash is unchanged but the revision advances.</summary>
    public Payload Apply(PayloadArchived archived) => this with { Status = PayloadStatus.Archived, Head = Head with { Revision = Head.Revision + 1 } };
}
