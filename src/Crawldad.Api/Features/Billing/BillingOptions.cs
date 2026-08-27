using Microsoft.Extensions.Options;

namespace Crawldad.Api.Features.Billing;

/// <summary>The billing subsystem knobs, bound from <c>Billing</c>. Two independent parts: the payment-provider
/// (Stripe) credentials — presence of BOTH keys is what "configured" means — and the tier catalog that maps a plan
/// moniker to its price/slot allowance and the provider price id a webhook resolves back to a tier. The catalog defaults
/// to <see cref="BillingTierCatalog.Defaults"/> (the BUSINESS_MODEL.md numbers) when config supplies none.</summary>
public sealed class BillingOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string Section = "Billing";

    /// <summary>The payment-provider (Stripe) credentials. Absent by default — an unconfigured deployment fails closed.</summary>
    public BillingStripeOptions Stripe { get; init; } = new();

    /// <summary>The absolute base URL the provider's hosted pages redirect back to (the portal's origin, e.g.
    /// <c>https://app.crawldad.io</c>). Empty by default, in which case the fake gateway builds <b>relative</b> in-app
    /// return paths — which the portal (the origin doing the redirect) resolves against itself. A real Stripe integration
    /// needs an absolute value here so Checkout/Portal success and cancel URLs are absolute.</summary>
    public string PortalReturnUrl { get; init; } = "";

    /// <summary>The tier catalog. When config supplies an empty list, <see cref="BillingTierCatalog.Defaults"/> apply.</summary>
    public IList<BillingTierConfig> Tiers { get; init; } = [];
}

/// <summary>The payment-provider credentials, bound from <c>Billing:Stripe</c>. Both a <see cref="SecretKey"/> (server
/// API calls) and a <see cref="WebhookSecret"/> (inbound event signature verification) must be present for billing to be
/// <see cref="IsConfigured"/>; either missing fails closed. Secrets — never logged, never returned on the wire.</summary>
public sealed class BillingStripeOptions
{
    /// <summary>The provider secret API key (<c>sk_live_…</c>). Blank/absent → not configured.</summary>
    public string SecretKey { get; init; } = "";

    /// <summary>The webhook signing secret (<c>whsec_…</c>) the inbound receiver verifies each event against. Blank/absent
    /// → not configured (and no inbound event can be accepted, since none can be verified).</summary>
    public string WebhookSecret { get; init; } = "";

    /// <summary>Whether both credentials are present — the precondition for any real provider call.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(SecretKey) && !string.IsNullOrWhiteSpace(WebhookSecret);
}

/// <summary>One tier in the billing catalog: its stable moniker (the enforced <see cref="Crawldad.Api.Infrastructure.Security.RegistryTenant"/> tier value and
/// the checkout target), a display name + price label for the portal, the included concurrent slot allowance the webhook
/// writes onto the tenant (null for a custom/committed tier), whether it is self-serve (has hosted checkout), and the
/// provider <see cref="PriceId"/> a subscription webhook resolves back to this tier.</summary>
public sealed class BillingTierConfig
{
    /// <summary>The stable tier moniker (e.g. <c>team</c>). Written to <see cref="Crawldad.Api.Infrastructure.Security.RegistryTenant.Tier"/> by the webhook.</summary>
    public string Tier { get; init; } = "";

    /// <summary>The human-facing plan name (e.g. "Team").</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>A display price (e.g. "$99/mo", "$0", "Custom").</summary>
    public string PriceLabel { get; init; } = "";

    /// <summary>The included concurrent run slots the webhook writes onto the tenant's <see cref="Crawldad.Api.Infrastructure.Security.RegistryTenant.SlotAllowance"/>,
    /// or null for a custom/committed tier (whose allowance is set out of band, not by a self-serve webhook).</summary>
    public int? Slots { get; init; }

    /// <summary>Whether this tier is purchasable via hosted checkout. False → the portal renders "contact sales" (Free and
    /// Enterprise), and a checkout-session request that targets it is a 400.</summary>
    public bool SelfServe { get; init; }

    /// <summary>The provider price id a <c>customer.subscription.*</c> webhook carries, mapped back to this tier by
    /// <see cref="BillingCatalog.ByPriceId"/>. Null for a non-self-serve tier (no price to buy).</summary>
    public string? PriceId { get; init; }
}

/// <summary>The BUSINESS_MODEL.md tier defaults, applied when <c>Billing:Tiers</c> configures none. Free (2 slots) and
/// Enterprise (custom) are not self-serve; Team ($99 / 10 slots) and Scale ($499 / 50 slots) have hosted checkout. The
/// <see cref="BillingTierConfig.PriceId"/>s are placeholders — a real deployment overrides them with live Stripe price
/// ids via config. Slot counts are the enforced numbers from docs/BUSINESS_MODEL.md.</summary>
public static class BillingTierCatalog
{
    /// <summary>The moniker of the baseline tier a cancelled subscription downgrades a tenant to.</summary>
    public const string FreeTier = "free";

    /// <summary>The default tier catalog (BUSINESS_MODEL.md).</summary>
    public static IReadOnlyList<BillingTierConfig> Defaults { get; } =
    [
        new() { Tier = FreeTier, DisplayName = "Free", PriceLabel = "$0", Slots = 2, SelfServe = false, PriceId = null },
        new() { Tier = "team", DisplayName = "Team", PriceLabel = "$99/mo", Slots = 10, SelfServe = true, PriceId = "price_team" },
        new() { Tier = "scale", DisplayName = "Scale", PriceLabel = "$499/mo", Slots = 50, SelfServe = true, PriceId = "price_scale" },
        new() { Tier = "enterprise", DisplayName = "Enterprise", PriceLabel = "Custom", Slots = null, SelfServe = false, PriceId = null },
    ];
}

/// <summary>The resolved, in-memory tier catalog: the configured <c>Billing:Tiers</c> when non-empty, else
/// <see cref="BillingTierCatalog.Defaults"/>. Centralizes the two lookups the slice needs — a tier by moniker (checkout +
/// config rendering) and a tier by provider price id (webhook price→tier→slots) — so neither endpoint re-derives them.</summary>
public sealed class BillingCatalog
{
    private readonly IReadOnlyList<BillingTierConfig> _tiers;

    /// <summary>Builds the catalog from bound options, falling back to the defaults when none are configured.</summary>
    public BillingCatalog(IOptions<BillingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configured = options.Value.Tiers;
        _tiers = configured.Count > 0 ? [.. configured] : BillingTierCatalog.Defaults;
    }

    /// <summary>The catalog, in display order.</summary>
    public IReadOnlyList<BillingTierConfig> Tiers => _tiers;

    /// <summary>The tier for a moniker (case-insensitive), or null when unknown.</summary>
    public BillingTierConfig? ByTier(string tier)
    {
        ArgumentNullException.ThrowIfNull(tier);
        return _tiers.FirstOrDefault(t => string.Equals(t.Tier, tier, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The tier a provider price id maps to (exact match), or null when the price is unrecognised — the
    /// webhook's "unknown price → drop" branch.</summary>
    public BillingTierConfig? ByPriceId(string? priceId) =>
        priceId is null ? null : _tiers.FirstOrDefault(t => t.PriceId is not null && string.Equals(t.PriceId, priceId, StringComparison.Ordinal));
}
