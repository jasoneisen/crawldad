using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Web.Infrastructure.Security;

/// <summary>
/// The authenticated tenant of the current HTTP request (CD-1), resolved from the principal the API-key handler issued.
/// Endpoints inject this to read the actor to stamp on a mutation event (<see cref="Actor"/>, §12) or the tenant to scope a
/// self-owned Marten session that Wolverine's per-request session tenanting cannot reach (<see cref="TenantId"/> — the SSE
/// backfill opens its own query sessions from the store). The request-scoped Marten sessions Wolverine injects are already
/// tenant-scoped by the same claim, so most endpoints never touch this. Only meaningful inside an authenticated request;
/// every route that resolves it requires an authenticated tenant, so the claims are always present.
/// </summary>
/// <param name="accessor">The ambient HTTP context accessor.</param>
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
