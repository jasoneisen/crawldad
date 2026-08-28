using Crawldad.Api.Features.Runs;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Wolverine.Http;

namespace Crawldad.Api.Features.Tenancy;

/// <summary><c>GET /tenant</c>: the authenticated tenant's own profile — its id, display-name identity, optional
/// pricing-tier label, and slot/queue-depth allowances. Resolved <b>registry-first, env-fallback</b>: a
/// signup/management-created <see cref="RegistryTenant"/> is authoritative for its display name, tier, and slot allowance
/// (queue depth defers to the global default, which the registry does not carry); an env-configured
/// <see cref="TenantDescriptor"/> sources them from the bound options, each override falling back to the global default.
/// No tenant-management surface is implied.</summary>
public static class TenantEndpoint
{
    /// <summary>Handles <c>GET /tenant</c>.</summary>
    [WolverineGet("/tenant")]
    public static async Task<IResult> Handle(
        TenantContext tenant,
        ITenantRegistryStore registry,
        IOptions<TenantOptions> tenants,
        IOptions<RunLimitsOptions> limits,
        CancellationToken ct)
    {
        // Registry-first, env-fallback (the same order the auth boundary resolves a key): a signup/management-created
        // tenant lives in the registry, NOT env config, so resolving with .First() over env options threw for it — a 500
        // that also broke the portal's link probe (GET /tenant). Resolve from whichever source owns the tenant.
        var registered = await registry.FindAsync(tenant.TenantId, ct);
        var descriptor = tenants.Value.Tenants.FirstOrDefault(t => string.Equals(t.Id, tenant.TenantId, StringComparison.Ordinal));

        return Results.Ok(TenantProfileResolution.Resolve(tenant.TenantId, tenant.Actor, registered, descriptor, limits.Value));
    }
}
