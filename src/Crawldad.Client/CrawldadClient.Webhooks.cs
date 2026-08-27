using System.Globalization;
using Crawldad.Contracts.Webhooks;

namespace Crawldad.Client;

/// <summary>Webhook-endpoint surface: register (or replace), list, read an endpoint's delivery history, and unregister.
/// The signing secret is write-only — sent on register, never returned by any endpoint.</summary>
public sealed partial class CrawldadClient
{
    /// <summary>Registers or replaces a webhook endpoint (<c>PUT /webhooks/{name}</c>). The response is the stored
    /// metadata only — never the secret.</summary>
    /// <param name="name">The endpoint name (a slug).</param>
    /// <param name="request">The url, signing secret, and subscribed events.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The stored webhook metadata.</returns>
    /// <exception cref="CrawldadValidationException">Invalid name slug, url (SSRF policy), secret, or event types (<c>400</c>).</exception>
    public Task<WebhookSummary> RegisterWebhookAsync(string name, RegisterWebhookRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<WebhookSummary>(HttpMethod.Put, $"webhooks/{Uri.EscapeDataString(name)}", request, ct);
    }

    /// <summary>Lists the tenant's registered webhook endpoints (<c>GET /webhooks</c>) — secrets omitted.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The webhook listing.</returns>
    public Task<WebhookListResponse> ListWebhooksAsync(CancellationToken ct = default) =>
        GetAsync<WebhookListResponse>("webhooks", ct);

    /// <summary>Reads one endpoint's recent delivery attempts (<c>GET /webhooks/{name}/deliveries</c>), newest first —
    /// each attempt (including a retry of the same event) is a distinct row, so a receiver's flakiness reads as its retry
    /// ladder. Never the signed body or the signing secret.</summary>
    /// <param name="name">The endpoint name.</param>
    /// <param name="limit">The optional page size (<c>?limit=N</c>), clamped server-side into 1..the retention cap; omit
    /// for the whole retained window.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The endpoint's recent deliveries.</returns>
    /// <exception cref="CrawldadNotFoundException">No such webhook for this tenant (<c>404</c>).</exception>
    public Task<WebhookDeliveryResponse> GetWebhookDeliveriesAsync(string name, int? limit = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var escaped = Uri.EscapeDataString(name);
        var path = limit is null
            ? $"webhooks/{escaped}/deliveries"
            : $"webhooks/{escaped}/deliveries?limit={limit.Value.ToString(CultureInfo.InvariantCulture)}";
        return GetAsync<WebhookDeliveryResponse>(path, ct);
    }

    /// <summary>Unregisters a webhook endpoint (<c>DELETE /webhooks/{name}</c>).</summary>
    /// <param name="name">The endpoint name.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="CrawldadNotFoundException">No such webhook for this tenant (<c>404</c>).</exception>
    public Task UnregisterWebhookAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return SendNoContentAsync(HttpMethod.Delete, $"webhooks/{Uri.EscapeDataString(name)}", ct);
    }
}
