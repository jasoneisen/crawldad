using Crawldad.Contracts.Tenancy;

namespace Crawldad.Client;

/// <summary>Self-serve free-tier provisioning (issue #119 PR7): the one API call a brand-new console user makes before they
/// have any workspace. It must ride the <b>console credential</b> — build the client with
/// <see cref="ConsoleCredential.ForProvisioning"/> (token + verified user, no workspace selector), since there is no workspace
/// yet; an API-key client is rejected (<c>401</c>). The portal calls this from its signup / "create your free workspace" flow.</summary>
public sealed partial class CrawldadClient
{
    /// <summary>Provisions the authenticated console user's ONE free-tier workspace (<c>POST /provisioning/tenants</c>) and
    /// records them as its Owner. One free workspace per email, <b>ever</b> — a second call is a <c>409</c>
    /// (<c>free_tenant_exists</c>) whose body carries the existing workspace id; additional workspaces are created on a paid
    /// plan. No API key is minted (a console user mints keys from the keys surface when needed).</summary>
    /// <param name="displayName">An optional human name for the new workspace; blank/absent → a server-side default.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The created workspace — tenant id, display name, and the caller's <c>Owner</c> role (the same
    /// <see cref="WorkspaceSummary"/> shape <see cref="ListMyWorkspacesAsync"/> returns), so the portal can select it at once.</returns>
    /// <exception cref="CrawldadUnauthorizedException">Not authenticated as the portal console identity — e.g. an API-key
    /// client, or a missing/invalid console token (<c>401</c>).</exception>
    /// <exception cref="CrawldadApiException">Already provisioned (<c>409 free_tenant_exists</c>), too many attempts
    /// (<c>429</c>), or the display name is too long (<c>400</c>). The raw body is on <see cref="CrawldadApiException.ResponseBody"/>.</exception>
    public Task<WorkspaceSummary> ProvisionTenantAsync(string? displayName = null, CancellationToken ct = default) =>
        SendJsonAsync<WorkspaceSummary>(HttpMethod.Post, "provisioning/tenants", new ProvisionTenantRequest(displayName), ct);
}
