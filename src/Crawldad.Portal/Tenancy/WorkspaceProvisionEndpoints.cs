using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Crawldad.Portal.Tenancy;

/// <summary>The "Create your free workspace" affordance's form handler (issue #119 PR7): the Account page posts here (static
/// SSR, antiforgery-protected, the same POST-then-redirect shape as the switcher / sign-out), it provisions the signed-in
/// account's one free workspace through <see cref="IPortalProvisioningService"/>, and redirects — to the dashboard (now scoped
/// to the new workspace) on success, or back to the account with a safe error message otherwise. The service is console-mode
/// only; in stored-key mode the affordance is never rendered, so a crafted post degrades to a clean "unavailable" error.</summary>
internal static class WorkspaceProvisionEndpoints
{
    /// <summary>Where a successful provision lands — the dashboard, scoped to the newly created/active workspace.</summary>
    internal const string DashboardPath = "/app/runs";

    /// <summary>The account page (an un-provisionable request, or an error, lands back here).</summary>
    internal const string AccountPath = "/app/account";

    internal static void MapWorkspaceProvision(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/app/workspace/provision", ProvisionAsync);

    internal static async Task<IResult> ProvisionAsync(
        HttpContext http,
        IPortalProvisioningService provisioning,
        [FromForm] string? displayName)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(provisioning);

        var email = http.User.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            return Results.LocalRedirect(AccountPath); // no signed-in identity → nothing to provision for
        }

        var result = await provisioning.ProvisionAsync(email, displayName, http.RequestAborted);
        return result.Outcome is PortalProvisionOutcome.Provisioned or PortalProvisionOutcome.AlreadyProvisioned
            ? Results.LocalRedirect(DashboardPath)                                                   // landed on the workspace
            : Results.LocalRedirect($"{AccountPath}?provisionError={Uri.EscapeDataString(result.Message)}"); // show the reason
    }
}
