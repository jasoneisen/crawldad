using Crawldad.Contracts.Payloads;
using Marten.Events.Aggregation;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// The async listing read model for managed payloads (§14.1): one summary document per payload, projected from its event
/// stream. This is the cross-payload dashboard/list source (async in production, lag tolerated — §11), distinct from the
/// <see cref="Payload"/> snapshot the query endpoints load for read-your-writes. Metadata only — no script body — so it
/// is never a credential-leak vector and stays small. Revision advances on every event (one event = one version); only a
/// revise changes the script hash. Rows are exposed as the <see cref="PayloadListItem"/> DTO, never this document.
/// </summary>
/// <param name="Id">The payload id (the event-stream id; assigned by Marten from the stream).</param>
/// <param name="Name">The payload's logical name.</param>
/// <param name="Revision">The head revision.</param>
/// <param name="ScriptHash">The head revision's script hash.</param>
/// <param name="Status">The lifecycle state.</param>
/// <param name="DraftedAt">When the payload was first drafted.</param>
/// <param name="UpdatedAt">When the head revision was produced.</param>
public sealed record PayloadSummary(
    Guid Id,
    string Name,
    int Revision,
    string ScriptHash,
    PayloadStatus Status,
    DateTimeOffset DraftedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Folds a payload's events into its <see cref="PayloadSummary"/> row. Registered on the shared, config-driven projection
/// lifecycle (Inline under the test switch, Async in production) alongside the aggregate snapshots (§14.1/HostConfiguration).
/// </summary>
public sealed partial class PayloadSummaryProjection : SingleStreamProjection<PayloadSummary, Guid>
{
    /// <summary>Creates the row on the drafting event (revision 1).</summary>
    /// <param name="drafted">The drafting event.</param>
    public PayloadSummary Create(PayloadDrafted drafted) =>
        new(default, drafted.Name, 1, drafted.ScriptHash, PayloadStatus.Active, drafted.DraftedAt, drafted.DraftedAt);

    /// <summary>Advances the row to a new script revision.</summary>
    /// <param name="revised">The revise event.</param>
    /// <param name="current">The current row.</param>
    public PayloadSummary Apply(PayloadRevised revised, PayloadSummary current) =>
        current with { Revision = current.Revision + 1, ScriptHash = revised.ScriptHash, UpdatedAt = revised.RevisedAt };

    /// <summary>Renames the row (script hash unchanged, revision advances).</summary>
    /// <param name="renamed">The rename event.</param>
    /// <param name="current">The current row.</param>
    public PayloadSummary Apply(PayloadRenamed renamed, PayloadSummary current) =>
        current with { Name = renamed.Name, Revision = current.Revision + 1, UpdatedAt = renamed.RenamedAt };

    /// <summary>Archives the row (script hash unchanged, revision advances).</summary>
    /// <param name="archived">The archive event.</param>
    /// <param name="current">The current row.</param>
    public PayloadSummary Apply(PayloadArchived archived, PayloadSummary current) =>
        current with { Status = PayloadStatus.Archived, Revision = current.Revision + 1, UpdatedAt = archived.ArchivedAt };
}
