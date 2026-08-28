using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Tenancy;

/// <summary>The <c>GET /tenant</c> response: the authenticated tenant's own profile. <see cref="TenantId"/> is the stable
/// billing/partition id and <see cref="DisplayName"/> its display identity (a registry tenant's display name, or an
/// env-configured tenant's actor). <see cref="Tier"/> is the optional pricing-tier label (omitted when unset).
/// <see cref="SlotAllowance"/> is the tenant's concurrent-run cap and <see cref="QueueDepthAllowance"/> its admission-queue
/// depth — each the per-tenant override when set, else the global default. Resolved registry-first (a signup/management-
/// created tenant) with a fallback to the bound tenant options. Distinct from the management API's <c>TenantResponse</c>
/// (a server-side tenant-administration record), hence the <c>Profile</c> name.</summary>
public sealed record TenantProfileResponse(
    string TenantId,
    string DisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tier,
    int SlotAllowance,
    int QueueDepthAllowance);
