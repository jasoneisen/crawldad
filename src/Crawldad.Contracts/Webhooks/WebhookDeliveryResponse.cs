using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Webhooks;

/// <summary>One recorded delivery attempt for a webhook endpoint (a row of <c>GET /webhooks/{name}/deliveries</c>): the
/// run and event type delivered, the 1-based <see cref="Attempt"/> number, whether the receiver accepted it
/// (<see cref="Delivered"/> = a 2xx), the observed HTTP <see cref="StatusCode"/> (omitted on a transport failure — a
/// connection error or timeout produced no response), the measured round-trip <see cref="LatencyMs"/>, and when the
/// attempt was made. Never the signed body or the signing secret.</summary>
public sealed record WebhookDeliveryItem(
    Guid RunId,
    string EventType,
    int Attempt,
    bool Delivered,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StatusCode,
    long LatencyMs,
    DateTimeOffset At);

/// <summary>The compact last-delivery summary attached to each <see cref="WebhookSummary"/> row on <c>GET /webhooks</c>:
/// the outcome of the endpoint's most recent delivery attempt, so a listing can show a "last delivery" column without a
/// second call. Absent when the endpoint has never been delivered to.</summary>
public sealed record WebhookDeliverySummary(
    Guid RunId,
    string EventType,
    int Attempt,
    bool Delivered,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StatusCode,
    long LatencyMs,
    DateTimeOffset At);

/// <summary>The <c>GET /webhooks/{name}/deliveries</c> response: the endpoint's recent delivery attempts, newest first,
/// capped by the retention policy (the latest N per endpoint). Each attempt — including retries of the same event — is a
/// distinct row, so a receiver's flakiness is visible as its retry ladder.</summary>
public sealed record WebhookDeliveryResponse(IReadOnlyList<WebhookDeliveryItem> Deliveries);
