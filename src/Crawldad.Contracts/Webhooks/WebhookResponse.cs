using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Webhooks;

/// <summary>One registered webhook endpoint as returned by <c>PUT /webhooks/{name}</c> and each row of <c>GET /webhooks</c>:
/// the metadata a tenant registered, <b>never the signing secret</b>. No field here is or derives from the secret, so a
/// listing is safe to surface in full. An empty <see cref="Events"/> means the endpoint receives all terminal-run events.
/// <see cref="LastDelivery"/> is the endpoint's most recent delivery outcome — additive, present on the <c>GET /webhooks</c>
/// listing when at least one delivery has been attempted, and omitted on the <c>PUT</c> register response (no delivery yet).</summary>
public sealed record WebhookSummary(
    string Name,
    string Url,
    IReadOnlyList<string> Events,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WebhookDeliverySummary? LastDelivery = null);

/// <summary>The <c>GET /webhooks</c> response: every webhook endpoint the authenticated tenant has registered, secrets omitted.</summary>
public sealed record WebhookListResponse(IReadOnlyList<WebhookSummary> Webhooks);
