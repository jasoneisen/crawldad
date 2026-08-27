using Crawldad.Client;
using Crawldad.Portal.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Crawldad.Portal.Billing;

/// <summary>The plain HTTP form handlers the billing card posts to (the same antiforgery-protected POST-then-redirect
/// shape as sign-out): resolve the signed-in user's tenant, ask the API — via the SDK — to mint a hosted-page URL, and
/// redirect the browser to it. Nothing here holds a payment-provider secret; the portal only follows the URL the API
/// returns. Any not-linked or API error becomes a safe redirect (to the account page, or the "not yet available" result
/// page), never a 500.</summary>
internal static class BillingUiEndpoints
{
    /// <summary>The account page (a not-linked post falls back here).</summary>
    internal const string AccountPath = "/app/account";

    /// <summary>The result page for a billing attempt that could not proceed (provider not yet available).</summary>
    internal const string UnavailableResult = "/app/account/billing-result?outcome=unavailable";

    internal static void MapBillingUi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/app/billing/checkout", CheckoutAsync);
        endpoints.MapPost("/app/billing/portal", PortalAsync);
    }

    /// <summary>Opens hosted checkout for a target tier: resolve tenant → SDK checkout-session → redirect to the URL.</summary>
    internal static async Task<IResult> CheckoutAsync(HttpContext http, IPortalTenantContext tenants, [FromForm] string? tier)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        var tenant = await tenants.TryResolveAsync(http.RequestAborted);
        if (tenant is null || string.IsNullOrWhiteSpace(tier))
        {
            return Results.LocalRedirect(AccountPath);
        }

        try
        {
            var session = await tenant.Client.CreateCheckoutSessionAsync(tier, http.RequestAborted);
            return Results.Redirect(session.Url); // gateway-produced (trusted); fake → relative in-app result page
        }
        catch (Exception ex) when (ex is CrawldadException or HttpRequestException)
        {
            return Results.LocalRedirect(UnavailableResult);
        }
    }

    /// <summary>Opens the hosted billing portal: resolve tenant → SDK portal-session → redirect to the URL.</summary>
    internal static async Task<IResult> PortalAsync(HttpContext http, IPortalTenantContext tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        var tenant = await tenants.TryResolveAsync(http.RequestAborted);
        if (tenant is null)
        {
            return Results.LocalRedirect(AccountPath);
        }

        try
        {
            var session = await tenant.Client.CreatePortalSessionAsync(http.RequestAborted);
            return Results.Redirect(session.Url);
        }
        catch (Exception ex) when (ex is CrawldadException or HttpRequestException)
        {
            return Results.LocalRedirect(UnavailableResult);
        }
    }
}
