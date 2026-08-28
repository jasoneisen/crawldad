using Crawldad.Client;
using Crawldad.Contracts.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Crawldad.Portal.Tenancy;

/// <summary>The member-management form handlers the account page's Members card posts to (issue #119 PR6): add a member,
/// change a member's role, remove a member. Each resolves the signed-in user's active workspace and calls the API through
/// the SDK, then redirects back (PRG) — surfacing any refusal as a friendly <c>?memberError=</c> the account page renders.
/// These are Owner-only <b>on the API</b> (the console channel enforces it, and the card only renders the controls to an
/// Owner), so a non-Owner post is refused server-side too. Per-row actions ride plain antiforgery-protected forms rather
/// than a Blazor <c>EditForm</c>, so a page with many members needs no per-row form binding.</summary>
internal static class MembersEndpoints
{
    /// <summary>The account page a member action redirects back to.</summary>
    internal const string AccountPath = "/app/account";

    internal static void MapPortalMembers(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/app/members/add", AddAsync);
        endpoints.MapPost("/app/members/remove", RemoveAsync);
        endpoints.MapPost("/app/members/role", ChangeRoleAsync);
    }

    internal static async Task<IResult> AddAsync(HttpContext http, IPortalTenantContext tenants, [FromForm] string? email, [FromForm] string? role)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tenants);
        var tenant = await tenants.TryResolveAsync(http.RequestAborted);
        if (tenant is null)
        {
            return Results.LocalRedirect(AccountPath);
        }

        if (string.IsNullOrWhiteSpace(email) || !TryRole(role, out var parsed))
        {
            return Redirect("Enter an email address and pick a valid role.");
        }

        return await ActAsync(() => tenant.Client.AddMembershipAsync(email, parsed, http.RequestAborted));
    }

    internal static async Task<IResult> RemoveAsync(HttpContext http, IPortalTenantContext tenants, [FromForm] Guid membershipId)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tenants);
        var tenant = await tenants.TryResolveAsync(http.RequestAborted);
        if (tenant is null)
        {
            return Results.LocalRedirect(AccountPath);
        }

        return await ActAsync(() => tenant.Client.RemoveMembershipAsync(membershipId, http.RequestAborted));
    }

    internal static async Task<IResult> ChangeRoleAsync(HttpContext http, IPortalTenantContext tenants, [FromForm] Guid membershipId, [FromForm] string? role)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tenants);
        var tenant = await tenants.TryResolveAsync(http.RequestAborted);
        if (tenant is null)
        {
            return Results.LocalRedirect(AccountPath);
        }

        if (!TryRole(role, out var parsed))
        {
            return Redirect("Pick a valid role.");
        }

        return await ActAsync(() => tenant.Client.ChangeMembershipRoleAsync(membershipId, parsed, http.RequestAborted));
    }

    // Runs a membership action and maps its outcome to a PRG redirect: success (or an already-gone 404) → the account page;
    // the last-Owner refusal (409) → a friendly message; any other API error or transport hiccup → a generic retry message.
    private static async Task<IResult> ActAsync(Func<Task> action)
    {
        try
        {
            await action();
            return Results.LocalRedirect(AccountPath);
        }
        catch (CrawldadNotFoundException)
        {
            return Results.LocalRedirect(AccountPath); // already removed — treat as done, the refreshed list reflects it
        }
        catch (CrawldadApiException ex) when (ex.StatusCode == StatusCodes.Status409Conflict)
        {
            return Redirect("A workspace must keep at least one Owner — promote another member to Owner first.");
        }
        catch (CrawldadException)
        {
            return Redirect("That didn't work. Check the details and try again.");
        }
        catch (HttpRequestException)
        {
            return Redirect("Couldn't reach the API. Try again shortly.");
        }
    }

    private static IResult Redirect(string error) =>
        Results.LocalRedirect(AccountPath + "?memberError=" + Uri.EscapeDataString(error));

    // Parses a form role value ("Owner"/"Member", any casing) to the enum, rejecting anything unrecognized.
    private static bool TryRole(string? role, out MembershipRole parsed) =>
        Enum.TryParse(role, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
}
