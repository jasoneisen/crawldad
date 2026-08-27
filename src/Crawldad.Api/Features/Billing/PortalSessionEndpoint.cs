using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Billing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace Crawldad.Api.Features.Billing;

/// <summary><c>POST /billing/portal-session</c>: mints a hosted Billing-Portal redirect URL so the tenant can manage its
/// payment method, invoices, and plan on the provider's own pages. Returns only a URL; the portal never holds a provider
/// secret. Unconfigured → a friendly <c>503</c> (never a 500).</summary>
public static class PortalSessionEndpoint
{
    /// <summary>Handles <c>POST /billing/portal-session</c>.</summary>
    [WolverinePost("/billing/portal-session")]
    // [FromServices]: this POST has no request body, so without the marker Wolverine would treat the first complex
    // parameter (the concrete tenant context) as the body to deserialize (the interface gateway is injected regardless).
    public static async Task<IResult> Handle([FromServices] TenantContext tenant, IBillingGateway gateway, CancellationToken ct)
    {
        if (!gateway.IsConfigured)
        {
            return BillingProblems.NotConfigured();
        }

        try
        {
            var session = await gateway.CreatePortalSessionAsync(tenant.TenantId, ct);
            return Results.Ok(new BillingSessionResponse(session.Url));
        }
        catch (BillingNotConfiguredException)
        {
            return BillingProblems.NotConfigured();
        }
    }
}
