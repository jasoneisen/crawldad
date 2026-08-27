using System.Globalization;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Crawldad.Api.Features.Webhooks;

/// <summary>Delivers one signed webhook to one endpoint, with bounded exponential-backoff retry. Handles the durable
/// <see cref="DeliverWebhook"/> message: resolves the endpoint (a drop if it was deregistered since enqueue), signs the
/// body with the endpoint secret under a fresh timestamp, POSTs it via the mockable <see cref="IWebhookSender"/>, and — on
/// a non-2xx or transport failure — cascades a delayed retry until the configured attempt cap, after which it abandons the
/// delivery with a warning. Delivery is at-least-once; the receiver dedupes on run id + status.</summary>
public static class DeliverWebhookHandler
{
    /// <summary>Attempts one delivery, returning a delayed retry (or nothing, on success/drop/exhaustion) to cascade.</summary>
    public static async Task<OutgoingMessages> Handle(
        DeliverWebhook message,
        IDocumentSession session,
        IWebhookEndpointStore store,
        IWebhookDeliveryStore deliveries,
        IWebhookSender sender,
        IOptions<WebhookOptions> options,
        TimeProvider clock,
        ILogger<DeliverWebhook> logger,
        CancellationToken ct)
    {
        var endpoint = await store.ResolveAsync(session, message.EndpointName, ct);
        if (endpoint is null)
        {
            return []; // deregistered since this delivery was enqueued — drop, don't retry (nothing to record: no attempt made)
        }

        var delivery = options.Value.Delivery;
        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        var signature = WebhookSignature.Compute(endpoint.Secret, timestamp, message.Body);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Crawldad-Event"] = message.EventType,
            ["X-Crawldad-Delivery"] = message.EventId,
            ["X-Crawldad-Timestamp"] = timestamp.ToString(CultureInfo.InvariantCulture),
            ["X-Crawldad-Signature"] = signature,
        };

        // Measure round-trip via the clock's high-resolution timestamp (real elapsed even when GetUtcNow is a frozen test
        // clock), so the recorded latency is honest in production and a stable >= 0 in tests.
        var startedAt = clock.GetTimestamp();
        var result = await sender.SendAsync(endpoint.Url, message.Body, headers, delivery.Timeout, ct);
        await RecordAsync(deliveries, session, message, result, clock.GetElapsedTime(startedAt), clock.GetUtcNow(), options.Value.DeliveryHistory.MaxPerEndpoint, ct);
        if (result.Delivered)
        {
            return []; // the receiver accepted it
        }

        if (message.Attempt >= delivery.MaxAttempts)
        {
            logger.LogWarning(
                "Webhook delivery to endpoint {Endpoint} for {Event} abandoned after {Attempts} attempts (last status {Status})",
                message.EndpointName, message.EventType, message.Attempt, result.StatusCode);
            return []; // exhausted — stop retrying
        }

        var messages = new OutgoingMessages();
        messages.Delay(message with { Attempt = message.Attempt + 1 }, WebhookRetryPolicy.Backoff(message.Attempt, delivery));
        return messages;
    }

    // Persists this attempt's outcome into the endpoint's delivery history on the handler's tenant-scoped session (the
    // Wolverine transaction commits it alongside any cascaded retry), pruning the endpoint's log to its cap. Every actual
    // attempt is recorded — a success, a will-retry non-delivery, and the final abandoned one — so the log shows the whole
    // retry ladder; only the deregistered-endpoint drop above records nothing (no attempt was made).
    private static Task RecordAsync(
        IWebhookDeliveryStore deliveries,
        IDocumentSession session,
        DeliverWebhook message,
        WebhookSendResult result,
        TimeSpan latency,
        DateTimeOffset at,
        int maxPerEndpoint,
        CancellationToken ct) =>
        deliveries.RecordAsync(
            session,
            new WebhookDelivery
            {
                Id = Guid.NewGuid(),
                EndpointName = message.EndpointName,
                RunId = message.RunId,
                EventType = message.EventType,
                Attempt = message.Attempt,
                Delivered = result.Delivered,
                StatusCode = result.StatusCode,
                LatencyMs = (long)latency.TotalMilliseconds,
                At = at,
            },
            maxPerEndpoint,
            ct);
}
