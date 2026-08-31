using System.Security.Claims;
using Crawldad.Client;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Crawldad.Portal.Tenancy;

/// <summary>The workspace switcher's form handler (issue #119): the shell's switcher posts the target workspace here, and
/// this persists it as the account's active-workspace selection, then redirects (the same antiforgery-protected
/// POST-then-redirect shape as sign-out / billing). The selection is a preference, not authority — it is stored only for a
/// workspace the user is actually a member of (the switcher only offers those, and the API's membership gate backstops a
/// crafted post: a non-member selection simply fails the console gate on the next read, never leaks). The switcher chrome
/// only ever renders for a multi-workspace user, so this is reached only when there is a real choice to make.</summary>
internal static class WorkspaceSwitchEndpoints
{
    /// <summary>Where a successful switch lands — the dashboard, now scoped to the newly active workspace.</summary>
    internal const string DashboardPath = "/app/runs";

    /// <summary>Where a switch with nothing to act on (not linked / no email) falls back.</summary>
    internal const string AccountPath = "/app/account";

    internal static void MapWorkspaceSwitch(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/app/workspace", SwitchAsync);

    internal static async Task<IResult> SwitchAsync(
        HttpContext http,
        IPortalTenantContext tenants,
        IPortalWorkspaceSelectionStore selections,
        [FromForm] string? workspace)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(selections);

        var email = http.User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(workspace))
        {
            return Results.LocalRedirect(AccountPath);
        }

        var tenant = await tenants.TryResolveAsync(http.RequestAborted);
        if (tenant is null)
        {
            return Results.LocalRedirect(AccountPath); // not linked → nothing to switch to
        }

        if (await IsMemberOfAsync(tenant, workspace, http.RequestAborted))
        {
            await selections.SetAsync(email, workspace, http.RequestAborted);
        }

        return Results.LocalRedirect(DashboardPath);
    }

    // Whether the resolved account is a member of the target workspace, from the authoritative API list (GET /workspaces,
    // keyed on the account's own identity — independent of the currently-active workspace). Console is the only mode.
    private static async Task<bool> IsMemberOfAsync(PortalTenant tenant, string workspace, CancellationToken ct)
    {
        try
        {
            var workspaces = await tenant.Client.ListMyWorkspacesAsync(ct);
            return (workspaces.Workspaces ?? []).Any(w => string.Equals(w.TenantId, workspace, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is CrawldadException or HttpRequestException)
        {
            return false; // couldn't verify → leave the current selection in place
        }
    }
}
