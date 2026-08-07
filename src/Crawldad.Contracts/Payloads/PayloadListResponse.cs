namespace Crawldad.Contracts.Payloads;

/// <summary>
/// One row of the <c>GET /payloads</c> listing (§14.1): a managed payload's identity and pinned head, plus its
/// draft/last-update timestamps. Projected from the async <c>PayloadSummary</c> read model — a Contracts DTO, never the
/// internal aggregate. Carries no script body (a listing needs only metadata; the body is fetched per revision).
/// </summary>
/// <param name="PayloadId">The payload's event-stream id.</param>
/// <param name="Name">The payload's logical name.</param>
/// <param name="Revision">The head revision.</param>
/// <param name="ScriptHash">The head revision's script hash (SHA-256, lowercase hex).</param>
/// <param name="Status">The lifecycle state.</param>
/// <param name="DraftedAt">When the payload was first drafted (through the <see cref="TimeProvider"/> seam).</param>
/// <param name="UpdatedAt">When the head revision was produced (through the <see cref="TimeProvider"/> seam).</param>
public sealed record PayloadListItem(
    Guid PayloadId,
    string Name,
    int Revision,
    string ScriptHash,
    PayloadStatus Status,
    DateTimeOffset DraftedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The <c>GET /payloads</c> response (§14.1): every managed payload's summary row.</summary>
/// <param name="Payloads">The payload summaries.</param>
public sealed record PayloadListResponse(IReadOnlyList<PayloadListItem> Payloads);
