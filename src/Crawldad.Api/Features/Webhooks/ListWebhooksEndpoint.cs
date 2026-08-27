using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Api.Features.Webhooks;

/// <summary><c>GET /webhooks</c>: list the authenticated tenant's registered webhook endpoints — name, url, subscribed
/// events, and timestamps. Never the secret: no field here is or derives from the signing secret, and the store only ever
/// reads this tenant's partition, so a listing can never surface another tenant's registrations.</summary>
public static class ListWebhooksEndpoint
{
    /// <summary>Handles <c>GET /webhooks</c>.</summary>
    [WolverineGet("/webhooks")]
    public static async Task<IResult> Handle(
        [FromServices] IWebhookEndpointStore store,
        [FromServices] TenantContext tenant,
        CancellationToken ct)
    {
        var webhooks = await store.ListAsync(tenant.TenantId, ct);
        return Results.Ok(new WebhookListResponse(webhooks));
    }
}
