using Crawldad.Contracts.Payloads;
using Marten.Events.Aggregation;

namespace Crawldad.Api.Features.Payloads;

/// <summary>Async listing read model: one summary per payload — lag is tolerated here. It is <b>not</b> the
/// read-your-writes view, and neither is the <see cref="Payload"/> snapshot: both ride the same config-driven lifecycle
/// (Async in production, Inline only under the test switch), and no production path reads the snapshot document at all.
/// Every single-payload read — <c>GET /payloads/{id}</c>, revise, rename, archive, drift — folds the stream live with
/// <c>Events.AggregateStreamAsync&lt;Payload&gt;</c>, which is race-immune and reads its own writes. Metadata only — no
/// script body, never a credential-leak vector. Revision advances on every event; only revise changes the script hash.
/// Exposed via <see cref="PayloadListItem"/>, never this type.</summary>
public sealed record PayloadSummary(
    Guid Id,
    string Name,
    int Revision,
    string ScriptHash,
    PayloadStatus Status,
    DateTimeOffset DraftedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Folds a payload's events into its <see cref="PayloadSummary"/> row. Registered on the shared, config-driven
/// projection lifecycle (Inline under the test switch, Async in production) alongside the aggregate snapshot.</summary>
public sealed class PayloadSummaryProjection : SingleStreamProjection<PayloadSummary, Guid>
{
    /// <summary>Creates the row on the drafting event (revision 1).</summary>
    public PayloadSummary Create(PayloadDrafted drafted) =>
        new(default, drafted.Name, 1, drafted.ScriptHash, PayloadStatus.Active, drafted.DraftedAt, drafted.DraftedAt);

    /// <summary>Advances the row to a new script revision.</summary>
    public PayloadSummary Apply(PayloadRevised revised, PayloadSummary current) =>
        current with { Revision = current.Revision + 1, ScriptHash = revised.ScriptHash, UpdatedAt = revised.RevisedAt };

    /// <summary>Renames the row (script hash unchanged, revision advances).</summary>
    public PayloadSummary Apply(PayloadRenamed renamed, PayloadSummary current) =>
        current with { Name = renamed.Name, Revision = current.Revision + 1, UpdatedAt = renamed.RenamedAt };

    /// <summary>Archives the row (script hash unchanged, revision advances).</summary>
    public PayloadSummary Apply(PayloadArchived archived, PayloadSummary current) =>
        current with { Status = PayloadStatus.Archived, Revision = current.Revision + 1, UpdatedAt = archived.ArchivedAt };
}
