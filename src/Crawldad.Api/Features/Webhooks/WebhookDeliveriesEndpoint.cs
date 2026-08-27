using System.Globalization;
using Crawldad.Contracts.Webhooks;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Wolverine.Http;

namespace Crawldad.Api.Features.Webhooks;

/// <summary><c>GET /webhooks/{name}/deliveries</c>: the recent delivery attempts for one of the tenant's webhook
/// endpoints, newest first. Each attempt — including a retry of the same event — is a distinct row, so a receiver's
/// flakiness reads as its retry ladder. Tenant-scoped like every read: an unknown or foreign endpoint name is a 404 with
/// no existence oracle (the request's Marten session only sees this tenant's registrations). Bounded by the delivery-log
/// retention cap; an optional <c>?limit=N</c> narrows the page (clamped to 1..the cap).</summary>
public static class WebhookDeliveriesEndpoint
{
    /// <summary>Handles <c>GET /webhooks/{name}/deliveries</c>.</summary>
    [WolverineGet("/webhooks/{name}/deliveries")]
    public static async Task<IResult> Handle(
        string name,
        IQuerySession session,
        IWebhookDeliveryStore deliveries,
        IOptions<WebhookOptions> options,
        HttpContext http,
        CancellationToken ct)
    {
        // 404 for an unregistered (or foreign) endpoint, exactly like unregister — no delivery-history oracle for a name
        // this tenant never registered.
        if (await session.LoadAsync<WebhookEndpoint>(name, ct) is null)
        {
            return Results.NotFound();
        }

        var cap = options.Value.DeliveryHistory.MaxPerEndpoint;
        var limit = Math.Clamp(ParseLimit(http, cap), 1, cap);
        var recent = await deliveries.RecentAsync(session, name, limit, ct);
        return Results.Ok(new WebhookDeliveryResponse(recent));
    }

    // A tolerant ?limit: absent or non-numeric reads as the retention cap (return the whole retained window); the caller
    // clamps it into 1..cap so a stray value never over- or under-reads.
    private static int ParseLimit(HttpContext http, int fallback) =>
        http.Request.Query.TryGetValue("limit", out var raw)
        && int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
