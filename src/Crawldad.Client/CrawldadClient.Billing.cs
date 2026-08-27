using Crawldad.Contracts.Billing;

namespace Crawldad.Client;

/// <summary>Billing surface: read the billing config (is the provider wired, the tenant's current tier, the tier catalog)
/// and mint hosted-page redirect URLs for checkout and the billing portal. The client only ever receives a URL to follow
/// — it never holds a provider secret, and it cannot change a tenant's plan (that lands only via a verified provider
/// webhook the server receives out of band). The portal's account area consumes these three calls.</summary>
public sealed partial class CrawldadClient
{
    /// <summary>Reads the tenant's billing config (<c>GET /billing/config</c>): whether the provider is configured,
    /// the current tier moniker, and the tier catalog for rendering the plan card.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The billing config.</returns>
    /// <exception cref="CrawldadUnauthorizedException">The API key is missing or not valid (<c>401</c>).</exception>
    public Task<BillingConfigResponse> GetBillingConfigAsync(CancellationToken ct = default) =>
        GetAsync<BillingConfigResponse>("billing/config", ct);

    /// <summary>Creates a hosted-checkout session for a tier upgrade (<c>POST /billing/checkout-session</c>) and returns
    /// the URL to redirect the browser to. It does not change the tenant's plan.</summary>
    /// <param name="tier">The target tier moniker (a self-serve tier from <see cref="GetBillingConfigAsync"/>).</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The redirect URL to open checkout.</returns>
    /// <exception cref="CrawldadValidationException">The tier is unknown or not purchasable (<c>400</c>).</exception>
    /// <exception cref="CrawldadApiException">Billing is not yet available for this deployment (<c>503</c>).</exception>
    public Task<BillingSessionResponse> CreateCheckoutSessionAsync(string tier, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tier);
        return SendJsonAsync<BillingSessionResponse>(HttpMethod.Post, "billing/checkout-session", new CheckoutSessionRequest(tier), ct);
    }

    /// <summary>Creates a hosted Billing-Portal session (<c>POST /billing/portal-session</c>) and returns the URL to
    /// redirect the browser to for managing payment method, invoices, and plan.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The redirect URL to open the billing portal.</returns>
    /// <exception cref="CrawldadApiException">Billing is not yet available for this deployment (<c>503</c>).</exception>
    public Task<BillingSessionResponse> CreatePortalSessionAsync(CancellationToken ct = default) =>
        PostAsync<BillingSessionResponse>("billing/portal-session", ct);
}
