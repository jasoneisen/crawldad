using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Billing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Api.Features.Billing;

/// <summary><c>POST /billing/checkout-session</c>: mints a hosted-checkout redirect URL for the tenant to subscribe to a
/// target tier. It returns <b>only a URL</b> — it never changes the tenant's plan, so a tenant cannot raise its own slot
/// allowance by calling this; the tier change lands only via a later verified provider webhook. When the provider is not
/// configured it is a friendly <c>503</c> (never a 500), so the portal can fall back to "billing not yet available".</summary>
public static class CheckoutSessionEndpoint
{
    /// <summary>Handles <c>POST /billing/checkout-session</c>.</summary>
    [WolverinePost("/billing/checkout-session")]
    public static async Task<IResult> Handle(
        CheckoutSessionRequest request,
        [FromServices] TenantContext tenant,
        IBillingGateway gateway,
        [FromServices] BillingCatalog catalog,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!gateway.IsConfigured)
        {
            return BillingProblems.NotConfigured();
        }

        // Only a known, self-serve tier can be purchased; Free/Enterprise (and anything unknown) are a 400.
        var tier = string.IsNullOrWhiteSpace(request.Tier) ? null : catalog.ByTier(request.Tier);
        if (tier is null || !tier.SelfServe)
        {
            return BillingProblems.UnknownTier(request.Tier);
        }

        try
        {
            var session = await gateway.CreateCheckoutSessionAsync(tenant.TenantId, tier, ct);
            return Results.Ok(new BillingSessionResponse(session.Url));
        }
        catch (BillingNotConfiguredException)
        {
            // Configured-but-unwired (the production stub with credentials present): stay never-500, same friendly state.
            return BillingProblems.NotConfigured();
        }
    }
}
