using Crawldad.Contracts.Webhooks;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Api.Features.Webhooks;

/// <summary><c>GET /webhooks</c>: list the authenticated tenant's registered webhook endpoints — name, url, subscribed
/// events, timestamps, and each endpoint's most recent delivery outcome (<c>lastDelivery</c>, additive; absent when the
/// endpoint has never been delivered to). Never the secret: no field here is or derives from the signing secret, and
/// every read rides the request's tenant-scoped session, so a listing can never surface another tenant's registrations.</summary>
public static class ListWebhooksEndpoint
{
    /// <summary>Handles <c>GET /webhooks</c>.</summary>
    [WolverineGet("/webhooks")]
    public static async Task<IResult> Handle(
        [FromServices] IWebhookEndpointStore endpoints,
        [FromServices] IWebhookDeliveryStore deliveries,
        IQuerySession session,
        CancellationToken ct)
    {
        var webhooks = await endpoints.ListAsync(session, ct);
        var latest = await deliveries.LatestPerEndpointAsync(session, ct);
        var enriched = webhooks.Select(webhook =>
            latest.TryGetValue(webhook.Name, out var last) ? webhook with { LastDelivery = last } : webhook);
        return Results.Ok(new WebhookListResponse([.. enriched]));
    }
}
