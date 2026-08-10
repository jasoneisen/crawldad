namespace Crawldad.Contracts.Payloads;

/// <summary>One row of the <c>GET /payloads</c> listing: a managed payload's identity, pinned head, and draft/update
/// timestamps — no script body (a listing is metadata only; the body is fetched per revision).</summary>
public sealed record PayloadListItem(
    Guid PayloadId,
    string Name,
    int Revision,
    string ScriptHash,
    PayloadStatus Status,
    DateTimeOffset DraftedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The <c>GET /payloads</c> response: every managed payload's summary row.</summary>
public sealed record PayloadListResponse(IReadOnlyList<PayloadListItem> Payloads);
