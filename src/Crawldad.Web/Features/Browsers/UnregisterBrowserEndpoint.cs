using Crawldad.Web.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Web.Features.Browsers;

/// <summary><c>DELETE /browsers/{name}</c>: unregister a browser credential for the authenticated tenant. <c>204</c> on
/// success, <c>404</c> when the tenant has no such name — including another tenant's name, which is simply absent in
/// this tenant's partition, so a cross-tenant delete is a plain not-found with no existence oracle.</summary>
public static class UnregisterBrowserEndpoint
{
    [WolverineDelete("/browsers/{name}")]
    public static async Task<IResult> Handle(
        string name,
        [FromServices] IBrowserCredentialStore store,
        [FromServices] TenantContext tenant,
        CancellationToken ct)
    {
        var deleted = await store.DeleteAsync(tenant.TenantId, name, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }
}
