using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The authenticated tenant of the current HTTP request, resolved from the principal the API-key handler
/// issued. Endpoints inject this to stamp <see cref="Actor"/> on a mutation event, or to scope a self-owned Marten
/// session (<see cref="TenantId"/>) that Wolverine's per-request tenanting can't reach, e.g. the SSE backfill.</summary>
public sealed class TenantContext(IHttpContextAccessor accessor)
{
    /// <summary>The current tenant's partition id (matches the request's Marten session tenant).</summary>
    public string TenantId => Claim(CrawldadClaims.TenantId);

    /// <summary>The current tenant's actor identity, for stamping onto mutation events.</summary>
    public string Actor => Claim(CrawldadClaims.Actor);

    private string Claim(string type) =>
        accessor.HttpContext?.User.FindFirstValue(type)
        ?? throw new InvalidOperationException($"no authenticated tenant on the current request (missing '{type}' claim)");
}
