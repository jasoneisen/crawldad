using System.Globalization;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Crawldad.Web.Features.Webhooks;

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
        IWebhookSender sender,
        IOptions<WebhookOptions> options,
        TimeProvider clock,
        ILogger<DeliverWebhook> logger,
        CancellationToken ct)
    {
        var endpoint = await store.ResolveAsync(session, message.EndpointName, ct);
        if (endpoint is null)
        {
            return []; // deregistered since this delivery was enqueued — drop, don't retry
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

        var result = await sender.SendAsync(endpoint.Url, message.Body, headers, delivery.Timeout, ct);
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
}
