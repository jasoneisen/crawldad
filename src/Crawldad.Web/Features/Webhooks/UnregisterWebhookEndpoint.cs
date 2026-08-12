using Crawldad.Web.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Web.Features.Webhooks;

/// <summary><c>DELETE /webhooks/{name}</c>: unregister a webhook endpoint for the authenticated tenant. <c>204</c> on
/// success, <c>404</c> when the tenant has no such name — including another tenant's name, which is simply absent in this
/// tenant's partition, so a cross-tenant delete is a plain not-found with no existence oracle.</summary>
public static class UnregisterWebhookEndpoint
{
    /// <summary>Handles <c>DELETE /webhooks/{name}</c>.</summary>
    [WolverineDelete("/webhooks/{name}")]
    public static async Task<IResult> Handle(
        string name,
        [FromServices] IWebhookEndpointStore store,
        [FromServices] TenantContext tenant,
        CancellationToken ct)
    {
        var deleted = await store.DeleteAsync(tenant.TenantId, name, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }
}
