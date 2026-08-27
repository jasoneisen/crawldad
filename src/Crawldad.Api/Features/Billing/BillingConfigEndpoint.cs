using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Billing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Wolverine.Http;

namespace Crawldad.Api.Features.Billing;

/// <summary><c>GET /billing/config</c>: whether billing is wired for this deployment, the authenticated tenant's current
/// tier, and the tier catalog — everything the portal needs to render the billing card (current plan, upgrade buttons,
/// or a friendly "not yet available" state) without duplicating the pricing numbers or holding any provider secret.
/// Read-only: it never changes the tenant's plan.</summary>
public static class BillingConfigEndpoint
{
    /// <summary>Handles <c>GET /billing/config</c>.</summary>
    [WolverineGet("/billing/config")]
    public static async Task<IResult> Handle(
        TenantContext tenant,
        IBillingGateway gateway,
        BillingCatalog catalog,
        ITenantRegistryStore registry,
        IOptions<TenantOptions> tenants,
        CancellationToken ct)
    {
        var currentTier = await CurrentTierAsync(tenant.TenantId, registry, tenants, ct);

        var tiers = catalog.Tiers
            .Select(t => new BillingTierOption(
                t.Tier, t.DisplayName, t.PriceLabel, t.Slots, t.SelfServe,
                IsCurrent: currentTier is not null && string.Equals(t.Tier, currentTier, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return Results.Ok(new BillingConfigResponse(gateway.IsConfigured, currentTier, tiers));
    }

    // The tenant's current tier moniker, resolved defensively from whichever source owns it: the DB registry tenant, else
    // the env-configured tenant descriptor. Empty/absent normalizes to null. Never throws for a tenant present in neither.
    private static async Task<string?> CurrentTierAsync(string tenantId, ITenantRegistryStore registry, IOptions<TenantOptions> tenants, CancellationToken ct)
    {
        var registryTier = (await registry.FindAsync(tenantId, ct))?.Tier;
        if (!string.IsNullOrWhiteSpace(registryTier))
        {
            return registryTier;
        }

        var envTier = tenants.Value.Tenants.FirstOrDefault(t => string.Equals(t.Id, tenantId, StringComparison.Ordinal))?.Tier;
        return string.IsNullOrWhiteSpace(envTier) ? null : envTier;
    }
}
