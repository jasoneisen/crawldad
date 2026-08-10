using Crawldad.Contracts.Browsers;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Web.Features.Browsers;

/// <summary><c>GET /browsers</c>: list the authenticated tenant's registered browsers — name, adapter, mode, options,
/// and timestamps. Never the secret: no field here is or derives from the credential value, and the store only ever
/// reads this tenant's partition, so a listing can never surface another tenant's registrations.</summary>
public static class ListBrowsersEndpoint
{
    [WolverineGet("/browsers")]
    public static async Task<IResult> Handle(
        [FromServices] IBrowserCredentialStore store,
        [FromServices] TenantContext tenant,
        CancellationToken ct)
    {
        var browsers = await store.ListAsync(tenant.TenantId, ct);
        return Results.Ok(new BrowserListResponse(browsers));
    }
}
