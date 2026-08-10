namespace Crawldad.Contracts.Browsers;

/// <summary>One registered browser as returned by <c>PUT /browsers/{name}</c> and each row of <c>GET /browsers</c>:
/// the metadata a tenant registered, never the secret. No field here is or derives from the credential value — a
/// listing is safe to surface in full.</summary>
public sealed record BrowserSummary(
    string Name,
    string Adapter,
    string Mode,
    IReadOnlyDictionary<string, string>? Options,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The <c>GET /browsers</c> response: every browser the authenticated tenant has registered, secrets omitted.</summary>
public sealed record BrowserListResponse(IReadOnlyList<BrowserSummary> Browsers);
