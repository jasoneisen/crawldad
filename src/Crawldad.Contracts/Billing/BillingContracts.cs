using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Billing;

/// <summary>The <c>POST /billing/checkout-session</c> body: the tier the tenant wants to move to. The endpoint only mints a
/// hosted-checkout redirect URL for this tier — it never changes the tenant's plan itself (a tenant must not be able to
/// upgrade its own slot allowance by calling an API). The actual tier change happens only when the payment provider later
/// posts a verified subscription webhook. <see cref="Tier"/> is a tier moniker from <c>GET /billing/config</c>
/// (<c>team</c>/<c>scale</c>/…); an unknown or non-self-serve tier is a <c>400</c>.</summary>
/// <param name="Tier">The target tier moniker to open checkout for.</param>
public sealed record CheckoutSessionRequest(string Tier);

/// <summary>The response for both <c>POST /billing/checkout-session</c> and <c>POST /billing/portal-session</c>: a single
/// <see cref="Url"/> the caller redirects the browser to (the provider-hosted Checkout page, or the hosted Billing Portal).
/// The portal never holds any payment-provider secret — it only follows this URL. With the development/test fake gateway
/// the URL is an in-app result page rather than a real hosted page.</summary>
/// <param name="Url">The redirect target.</param>
public sealed record BillingSessionResponse(string Url);

/// <summary>One tier in the billing catalog surfaced by <c>GET /billing/config</c>. Everything the portal needs to render a
/// plan card without duplicating the pricing numbers: the moniker, a display name, a price label, the included concurrent
/// slot count (null for a custom/Enterprise tier), whether it is self-serve (has hosted checkout) or "contact sales", and
/// whether it is the tenant's current tier.</summary>
/// <param name="Tier">The stable tier moniker (matches <see cref="CheckoutSessionRequest.Tier"/>).</param>
/// <param name="DisplayName">The human-facing plan name (e.g. "Team").</param>
/// <param name="PriceLabel">A display price (e.g. "$99/mo", "$0", "Custom").</param>
/// <param name="Slots">The included concurrent run slots, or null for a custom/committed tier.</param>
/// <param name="SelfServe">Whether this tier can be purchased via hosted checkout (false → "contact sales").</param>
/// <param name="IsCurrent">Whether this is the authenticated tenant's current tier.</param>
public sealed record BillingTierOption(
    string Tier,
    string DisplayName,
    string PriceLabel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Slots,
    bool SelfServe,
    bool IsCurrent);

/// <summary>The <c>GET /billing/config</c> response: whether billing is wired for this deployment, the tenant's current
/// tier moniker (null when the tenant is on no explicit tier), and the tier catalog. <see cref="Configured"/> is false
/// when the payment provider is not yet set up (the production stub is unconfigured) — the portal renders a friendly
/// "billing not yet available" state and never calls the session endpoints. The catalog is still returned so the portal
/// can show the plan ladder either way.</summary>
/// <param name="Configured">Whether the payment provider is configured (self-serve checkout/portal are usable).</param>
/// <param name="CurrentTier">The tenant's current tier moniker, or null.</param>
/// <param name="Tiers">The tier catalog, in display order.</param>
public sealed record BillingConfigResponse(
    bool Configured,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CurrentTier,
    IReadOnlyList<BillingTierOption> Tiers);
