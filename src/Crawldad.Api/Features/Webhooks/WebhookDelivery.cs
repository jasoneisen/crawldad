namespace Crawldad.Api.Features.Webhooks;

/// <summary>One recorded delivery attempt to one endpoint, persisted as a plain tenant-scoped Marten document (the shared
/// <c>AllDocumentsAreMultiTenanted</c> policy qualifies every row by tenant, so a tenant only ever sees its own delivery
/// history). Written by <see cref="DeliverWebhookHandler"/> after each attempt — including a retry of the same event, so
/// the full retry ladder is observable — and pruned to the latest N per endpoint. Carries no secret and never the signed
/// body: only the outcome facts a "last delivery" / delivery-log view needs.</summary>
public sealed class WebhookDelivery
{
    /// <summary>The per-attempt record id (a fresh GUID, the document id).</summary>
    public Guid Id { get; set; }

    /// <summary>The registered endpoint name this attempt targeted (the <see cref="WebhookEndpoint"/> id).</summary>
    public string EndpointName { get; set; } = "";

    /// <summary>The run whose terminal event was being delivered.</summary>
    public Guid RunId { get; set; }

    /// <summary>The event type (<c>run.succeeded</c> / <c>run.failed</c> / <c>run.cancelled</c>).</summary>
    public string EventType { get; set; } = "";

    /// <summary>The 1-based attempt number (1 on the first send, incremented per retry).</summary>
    public int Attempt { get; set; }

    /// <summary>Whether the receiver accepted the delivery (a 2xx response).</summary>
    public bool Delivered { get; set; }

    /// <summary>The observed HTTP status, or null when the request never got a response (a transport fault or timeout).</summary>
    public int? StatusCode { get; set; }

    /// <summary>The measured round-trip latency of the attempt, in milliseconds.</summary>
    public long LatencyMs { get; set; }

    /// <summary>When the attempt was made.</summary>
    public DateTimeOffset At { get; set; }
}
