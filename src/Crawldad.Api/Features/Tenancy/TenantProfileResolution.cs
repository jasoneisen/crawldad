using Crawldad.Api.Features.Runs;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>Resolves the authenticated tenant's read-only profile fields — display name, tier, and the slot/queue-depth
/// allowances — for <c>GET /tenant</c> and <c>GET /usage</c>, <b>registry-first, env-fallback</b>: the same order the
/// auth boundary itself resolves a presented key, and the same template <c>BillingConfigEndpoint</c> uses for the current
/// tier. A DB-backed <see cref="RegistryTenant"/> (every signup/management-created tenant) is authoritative for its own
/// display name, tier, and slot allowance; the registry carries no queue-depth field, so that allowance always defers to
/// the global default. An env-configured <see cref="TenantDescriptor"/> sources them from the bound options, each override
/// falling back to the global default. A tenant present in <b>neither</b> resolves to an all-default profile rather than
/// throwing — the guard that lets a registry tenant (absent from env config) be read at all. The previous <c>.First()</c>
/// over env options could not: it threw for a registry tenant, surfacing as a 500 that also broke the portal's link
/// probe (<c>WorkspaceLinker</c> validates a key by reading <c>GET /tenant</c>).</summary>
internal static class TenantProfileResolution
{
    /// <summary>The full <see cref="TenantProfileResponse"/> for the authenticated tenant. <paramref name="registered"/>
    /// is its registry document (null when it is not a registry tenant); <paramref name="descriptor"/> is its env-config
    /// descriptor (null when it is not env-configured); <paramref name="fallbackActor"/> is the actor from the principal,
    /// used as the display name only when the tenant is in neither source.</summary>
    internal static TenantProfileResponse Resolve(
        string tenantId,
        string fallbackActor,
        RegistryTenant? registered,
        TenantDescriptor? descriptor,
        RunLimitsOptions limits)
    {
        var slotAllowance = SlotAllowance(registered, descriptor, limits);

        // Registry-first: a management/signup-created tenant owns its display name, tier, and slot allowance. It has no
        // queue-depth field, so that allowance defers to the global default (documented in docs/API.md).
        if (registered is not null)
        {
            return new TenantProfileResponse(
                tenantId,
                registered.DisplayName,
                NormalizeTier(registered.Tier),
                slotAllowance,
                limits.MaxQueueDepthPerTenant);
        }

        // Env-fallback: a configured tenant sources its actor/tier/overrides from the bound options, each override falling
        // back to the global default; a tenant present in neither takes the principal's actor and the global defaults.
        return new TenantProfileResponse(
            tenantId,
            descriptor?.Actor ?? fallbackActor,
            NormalizeTier(descriptor?.Tier),
            slotAllowance,
            descriptor?.MaxQueueDepth ?? limits.MaxQueueDepthPerTenant);
    }

    /// <summary>The tenant's effective concurrent-run slot allowance: the registry override, else the env override, else
    /// the global default. Shared by <c>GET /tenant</c> and <c>GET /usage</c> so both report the identical cap.</summary>
    internal static int SlotAllowance(RegistryTenant? registered, TenantDescriptor? descriptor, RunLimitsOptions limits) =>
        registered?.SlotAllowance ?? descriptor?.MaxConcurrentRuns ?? limits.MaxConcurrentRunsPerTenant;

    // An empty/whitespace tier moniker is surfaced as "no tier" (omitted from the response) — env descriptors leave it
    // null, but a registry document defaults it to "", so both normalize to the same absent-tier shape.
    private static string? NormalizeTier(string? tier) => string.IsNullOrWhiteSpace(tier) ? null : tier;
}
