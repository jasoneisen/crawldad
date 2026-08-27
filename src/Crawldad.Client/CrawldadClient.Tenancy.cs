using Crawldad.Contracts.Tenancy;

namespace Crawldad.Client;

/// <summary>Tenancy read surface: the authenticated tenant's own profile and its live usage against its guardrails.
/// Both are read-only, tenant-scoped, and computed on read — there is deliberately no tenant-management endpoint here
/// (that lives on the separate management API). The portal's account area consumes these two calls.</summary>
public sealed partial class CrawldadClient
{
    /// <summary>Reads the authenticated tenant's own profile (<c>GET /tenant</c>): its id, display (actor) name,
    /// optional pricing-tier label, and slot / queue-depth allowances. The tenant is derived from the API key — there
    /// is no id parameter — so this doubles as the cheapest authenticated round-trip to confirm a key is valid.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The tenant's profile.</returns>
    /// <exception cref="CrawldadUnauthorizedException">The API key is missing or not valid (<c>401</c>).</exception>
    public Task<TenantProfileResponse> GetTenantAsync(CancellationToken ct = default) =>
        GetAsync<TenantProfileResponse>("tenant", ct);

    /// <summary>Reads the tenant's live usage against its guardrails (<c>GET /usage</c>): slot occupancy now, queue
    /// depth + p95 wait, runs started this calendar month, and events-per-run over a recent window. Pragmatic and
    /// approximate by design — a point-in-time reading, not a billing ledger.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The tenant's usage snapshot.</returns>
    /// <exception cref="CrawldadUnauthorizedException">The API key is missing or not valid (<c>401</c>).</exception>
    public Task<UsageResponse> GetUsageAsync(CancellationToken ct = default) =>
        GetAsync<UsageResponse>("usage", ct);
}
