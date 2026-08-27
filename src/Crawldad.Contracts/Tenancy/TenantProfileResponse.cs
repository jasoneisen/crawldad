using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Tenancy;

/// <summary>The <c>GET /tenant</c> response: the authenticated tenant's own profile. <see cref="TenantId"/> is the stable
/// billing/partition id and <see cref="DisplayName"/> its configured actor identity. <see cref="Tier"/> is the optional
/// configured pricing-tier label (omitted when unset). <see cref="SlotAllowance"/> is the tenant's concurrent-run cap and
/// <see cref="QueueDepthAllowance"/> its admission-queue depth — each the per-tenant override when configured, else the
/// global default. Resolved today from the bound tenant options; a future tenant-registry would back the same shape.
/// Distinct from the management API's <c>TenantResponse</c> (a server-side tenant-administration record), hence the
/// <c>Profile</c> name.</summary>
public sealed record TenantProfileResponse(
    string TenantId,
    string DisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Tier,
    int SlotAllowance,
    int QueueDepthAllowance);
