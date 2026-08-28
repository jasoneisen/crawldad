using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>The cross-tenant "which workspaces are mine" read (issue #119 PR6) that backs the portal's workspace switcher.
/// <c>GET /workspaces</c> is a console <b>read</b> (the <see cref="ConsoleAuthModule.ConsoleOrKeyPolicy"/>): it is authorized
/// like any other console read — the caller must present a valid console token and a workspace selector it is a member of —
/// but it returns the memberships of the <b>authenticated actor</b> (the human email the console scheme stamps), not of the
/// selected workspace. So a user proves membership in one workspace to enumerate all of theirs; the actor is never taken from
/// a request body, so it can only ever list the caller's own workspaces.
///
/// <para>On the API-key channel the actor is the tenant's <see cref="RegistryTenant.Actor"/> (not an email), so a key caller
/// gets an empty (or coincidental) list — the switcher is a portal/console feature. Each workspace's display name is joined
/// from its <see cref="RegistryTenant"/>; a membership whose tenant has vanished is skipped rather than surfaced.</para></summary>
public static class WorkspacesEndpoint
{
    /// <summary>Handles <c>GET /workspaces</c>: the authenticated user's active workspaces (newest membership first), each
    /// with its display name and the user's role — the switcher's source of truth in console mode.</summary>
    [WolverineGet("/workspaces")]
    public static async Task<IResult> List(
        TenantContext tenant,
        ITenantMembershipStore memberships,
        ITenantRegistryStore registry,
        CancellationToken ct)
    {
        // The actor is the authenticated identity: the human email on the console channel (the switcher's real use), or the
        // tenant's registry actor on the key channel. Either way it names WHO is asking — never a request-supplied value.
        var memberOf = await memberships.ListForEmailAsync(tenant.Actor, ct);

        var workspaces = new List<WorkspaceSummary>(memberOf.Count);
        foreach (var membership in memberOf)
        {
            var registryTenant = await registry.FindAsync(membership.TenantId, ct);
            if (registryTenant is null)
            {
                continue; // a membership whose workspace no longer exists — skip it rather than surface a dangling row
            }

            workspaces.Add(new WorkspaceSummary(membership.TenantId, registryTenant.DisplayName, membership.Role));
        }

        return Results.Ok(new WorkspaceList(workspaces));
    }
}
