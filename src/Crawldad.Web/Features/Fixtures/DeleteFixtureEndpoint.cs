using Crawldad.Web.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Web.Features.Fixtures;

/// <summary><c>DELETE /fixtures/{name}</c>: erase a tenant's recorded fixture set — the manifest and all its page HTML in
/// one tenant-scoped transaction. <c>204</c> on success, <c>404</c> when the tenant has no such name — including another
/// tenant's name, which is simply absent in this tenant's partition, so a cross-tenant delete is a plain not-found.</summary>
public static class DeleteFixtureEndpoint
{
    [WolverineDelete("/fixtures/{name}")]
    public static async Task<IResult> Handle(
        string name,
        [FromServices] IFixtureStore store,
        [FromServices] TenantContext tenant,
        CancellationToken ct)
    {
        var deleted = await store.DeleteAsync(tenant.TenantId, name, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }
}
