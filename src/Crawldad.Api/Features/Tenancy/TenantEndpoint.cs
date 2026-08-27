using Crawldad.Api.Features.Runs;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Wolverine.Http;

namespace Crawldad.Api.Features.Tenancy;

/// <summary><c>GET /tenant</c>: the authenticated tenant's own profile — its id, display (actor) identity, optional
/// pricing-tier label, and slot/queue-depth allowances. The allowances resolve from the existing per-tenant override
/// options (each override, or the global default when unset); no tenant-management surface is implied. If a tenant
/// registry lands later it can back this same shape without changing the wire contract.</summary>
public static class TenantEndpoint
{
    /// <summary>Handles <c>GET /tenant</c>.</summary>
    [WolverineGet("/tenant")]
    public static IResult Handle(TenantContext tenant, IOptions<TenantOptions> tenants, IOptions<RunLimitsOptions> limits)
    {
        // The authenticated principal is always one of the configured tenants (its id came from the registry built from
        // these same options), so the descriptor is present — its actor is the display name, its (optional) overrides the
        // allowances, each falling back to the global default.
        var descriptor = tenants.Value.Tenants.First(t => string.Equals(t.Id, tenant.TenantId, StringComparison.Ordinal));
        var slotAllowance = descriptor.MaxConcurrentRuns ?? limits.Value.MaxConcurrentRunsPerTenant;
        var queueDepthAllowance = descriptor.MaxQueueDepth ?? limits.Value.MaxQueueDepthPerTenant;

        return Results.Ok(new TenantProfileResponse(tenant.TenantId, descriptor.Actor, descriptor.Tier, slotAllowance, queueDepthAllowance));
    }
}
