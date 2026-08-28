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

    // Reads exactly one value for the claim. Zero ⇒ no authenticated tenant. More than one DISTINCT value ⇒ two schemes
    // merged into one principal (the finding #4 hazard) — fail closed rather than silently pick the first, so an ambiguous
    // request can never resolve to a tenant it only half-proved. The console path forbids the merge upstream (a request
    // presenting both a console token and an API key is rejected), so this guard never trips in normal operation.
    private string Claim(string type)
    {
        var user = accessor.HttpContext?.User;
        var values = user?.FindAll(type).Select(claim => claim.Value).Distinct(StringComparer.Ordinal).ToList() ?? [];
        return values.Count switch
        {
            1 => values[0],
            0 => throw new InvalidOperationException($"no authenticated tenant on the current request (missing '{type}' claim)"),
            _ => throw new InvalidOperationException($"ambiguous tenant on the current request (multiple distinct '{type}' claims)"),
        };
    }
}
