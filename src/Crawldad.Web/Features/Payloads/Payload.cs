namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// The Payload aggregate (§14.1): an anemic snapshot folded from the payload's event stream, whose stream <em>is</em>
/// its version history. This work package drafts revision 1 (<see cref="PayloadDrafted"/>); revise/rename/archive,
/// the <c>PayloadSummary</c> read model, and drift land in Phase 5. Decisions live in the endpoint, not here.
/// </summary>
/// <param name="Id">The payload id (the event stream id).</param>
/// <param name="Name">The payload's logical name.</param>
/// <param name="Revision">The head revision (1 at draft; increments per <c>PayloadRevised</c> in Phase 5).</param>
/// <param name="ScriptHash">SHA-256 (lowercase hex) of the head revision's script — pins exactly what was saved.</param>
public sealed record Payload(Guid Id, string Name, int Revision, string ScriptHash)
{
    /// <summary>Folds the drafting event into a fresh aggregate (Marten assigns <see cref="Id"/> from the stream).</summary>
    /// <param name="drafted">The drafting event.</param>
    public static Payload Create(PayloadDrafted drafted) => new(Guid.Empty, drafted.Name, 1, drafted.ScriptHash);
}
