namespace Crawldad.Contracts.Webhooks;

/// <summary>One registered webhook endpoint as returned by <c>PUT /webhooks/{name}</c> and each row of <c>GET /webhooks</c>:
/// the metadata a tenant registered, <b>never the signing secret</b>. No field here is or derives from the secret, so a
/// listing is safe to surface in full. An empty <see cref="Events"/> means the endpoint receives all terminal-run events.</summary>
public sealed record WebhookSummary(
    string Name,
    string Url,
    IReadOnlyList<string> Events,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The <c>GET /webhooks</c> response: every webhook endpoint the authenticated tenant has registered, secrets omitted.</summary>
public sealed record WebhookListResponse(IReadOnlyList<WebhookSummary> Webhooks);
