using Crawldad.Contracts.Tenancy;

namespace Crawldad.Client;

/// <summary>Tenant self-service membership surface (issue #119): the console authorization store, managed with the
/// tenant's own key. The portal's attach flow records the signed-in user's owner membership after proving key
/// possession; the account area lists memberships to show console-access state. Registry tenants only.</summary>
public sealed partial class CrawldadClient
{
    /// <summary>Records (idempotently) an owner membership for <paramref name="email"/> in the authenticated tenant
    /// (<c>POST /tenant/memberships</c>) — the console authority a later console read for that email resolves against. A
    /// re-record returns the existing active membership unchanged.</summary>
    /// <param name="email">The verified portal email to grant this workspace to (normalized server-side).</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The active owner membership.</returns>
    /// <exception cref="CrawldadValidationException">The email was missing (<c>400</c>).</exception>
    /// <exception cref="CrawldadApiException">This is an env-configured tenant (<c>400 self_service_unavailable</c>).</exception>
    public Task<TenantMembershipInfo> RecordOwnerMembershipAsync(string email, CancellationToken ct = default) =>
        SendJsonAsync<TenantMembershipInfo>(HttpMethod.Post, "tenant/memberships", new RecordMembershipRequest(email), ct);

    /// <summary>Lists the authenticated tenant's console memberships (<c>GET /tenant/memberships</c>) — each a verified
    /// email mapped to a role, newest first. Metadata only.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The tenant's memberships.</returns>
    /// <exception cref="CrawldadApiException">This is an env-configured tenant (<c>400 self_service_unavailable</c>).</exception>
    public Task<TenantMembershipList> ListMembershipsAsync(CancellationToken ct = default) =>
        GetAsync<TenantMembershipList>("tenant/memberships", ct);
}
