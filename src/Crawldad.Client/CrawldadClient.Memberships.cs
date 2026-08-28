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
    /// email mapped to a role, newest first. Metadata only. A console Member may read this; management (below) is Owner-only.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The tenant's memberships.</returns>
    /// <exception cref="CrawldadApiException">This is an env-configured tenant (<c>400 self_service_unavailable</c>).</exception>
    public Task<TenantMembershipList> ListMembershipsAsync(CancellationToken ct = default) =>
        GetAsync<TenantMembershipList>("tenant/memberships", ct);

    /// <summary>Adds (idempotently) a member to the authenticated tenant (<c>POST /tenant/memberships</c>) with
    /// <paramref name="role"/> — an Owner inviting a teammate. Owner-only on the console channel. A re-add of an already-active
    /// member returns it unchanged (its role is not altered — use <see cref="ChangeMembershipRoleAsync"/>).</summary>
    /// <param name="email">The member's email (normalized server-side).</param>
    /// <param name="role">The role to grant.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The active membership.</returns>
    /// <exception cref="CrawldadValidationException">The email was missing (<c>400</c>).</exception>
    /// <exception cref="CrawldadApiException">This is an env-configured tenant (<c>400 self_service_unavailable</c>).</exception>
    public Task<TenantMembershipInfo> AddMembershipAsync(string email, MembershipRole role, CancellationToken ct = default) =>
        SendJsonAsync<TenantMembershipInfo>(HttpMethod.Post, "tenant/memberships", new RecordMembershipRequest(email, role), ct);

    /// <summary>Removes (revokes) a membership (<c>DELETE /tenant/memberships/{id}</c>). Owner-only on the console channel.
    /// Self-removal is allowed; the tenant's last active Owner cannot be removed (<c>409 last_owner</c>).</summary>
    /// <param name="membershipId">The membership record id to remove.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="CrawldadNotFoundException">No such active membership for this tenant (<c>404</c>).</exception>
    /// <exception cref="CrawldadApiException">Removing the last Owner (<c>409 last_owner</c>), or this is an env-configured tenant (<c>400</c>).</exception>
    public Task RemoveMembershipAsync(Guid membershipId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"tenant/memberships/{membershipId}", ct);

    /// <summary>Sets a membership's role (<c>POST /tenant/memberships/{id}/role</c>). Owner-only on the console channel.
    /// Downgrading the tenant's last active Owner to a Member is refused (<c>409 last_owner</c>).</summary>
    /// <param name="membershipId">The membership record id to change.</param>
    /// <param name="role">The role to set.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The updated membership.</returns>
    /// <exception cref="CrawldadNotFoundException">No such active membership for this tenant (<c>404</c>).</exception>
    /// <exception cref="CrawldadApiException">Downgrading the last Owner (<c>409 last_owner</c>), or this is an env-configured tenant (<c>400</c>).</exception>
    public Task<TenantMembershipInfo> ChangeMembershipRoleAsync(Guid membershipId, MembershipRole role, CancellationToken ct = default) =>
        SendJsonAsync<TenantMembershipInfo>(HttpMethod.Post, $"tenant/memberships/{membershipId}/role", new ChangeMembershipRoleRequest(role), ct);

    /// <summary>Lists the workspaces the authenticated user can act as (<c>GET /workspaces</c>) — every tenant they hold an
    /// active membership in, with its display name and their role, newest membership first. The console-mode source for the
    /// portal's workspace switcher (issue #119 PR6). On the API-key channel this reflects the key's own tenant actor, so it is
    /// typically empty — the switcher is a portal/console feature.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The caller's workspaces.</returns>
    public Task<WorkspaceList> ListMyWorkspacesAsync(CancellationToken ct = default) =>
        GetAsync<WorkspaceList>("workspaces", ct);
}
