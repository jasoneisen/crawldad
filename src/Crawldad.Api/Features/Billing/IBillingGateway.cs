namespace Crawldad.Api.Features.Billing;

/// <summary>How a subscription changed, as reported by an inbound provider webhook, normalized away from the provider's
/// own event-type strings: a new subscription, a changed one (plan switch), or a cancelled one (downgrade to free).</summary>
public enum BillingSubscriptionChange
{
    /// <summary>A subscription was created (<c>customer.subscription.created</c>).</summary>
    Created,

    /// <summary>A subscription was updated — typically a plan switch (<c>customer.subscription.updated</c>).</summary>
    Updated,

    /// <summary>A subscription was cancelled (<c>customer.subscription.deleted</c>) — the tenant downgrades to free.</summary>
    Cancelled,
}

/// <summary>A verified, provider-agnostic subscription event the webhook endpoint acts on. <see cref="EventId"/> is the
/// provider's unique event id (the anti-replay dedup key). <see cref="TenantId"/> is authoritative — it comes from the
/// provider's own subscription metadata, never from an authenticated caller — so a tenant can never move another tenant's
/// (or its own) plan through the API. <see cref="PriceId"/> is the subscribed price (mapped to a tier), null on a
/// cancellation.</summary>
/// <param name="EventId">The provider event id (dedup key).</param>
/// <param name="Change">The normalized subscription change.</param>
/// <param name="TenantId">The Crawldad tenant id from the subscription metadata (authoritative).</param>
/// <param name="PriceId">The subscribed provider price id, or null on a cancellation.</param>
public sealed record BillingWebhookEvent(string EventId, BillingSubscriptionChange Change, string TenantId, string? PriceId);

/// <summary>A hosted-page redirect the caller sends the browser to: a provider Checkout page or the hosted Billing Portal.</summary>
/// <param name="Url">The redirect target.</param>
public readonly record struct BillingSession(string Url);

/// <summary>The payment-provider seam — the single abstraction the billing endpoints sit behind, so the portal never
/// holds a provider secret and the suite never touches a live provider. Two implementations:
/// <see cref="FakeBillingGateway"/> (development/tests — deterministic, in-process) and <see cref="StripeBillingGateway"/>
/// (production — a fail-closed stub until the Stripe SDK is wired). All three operations are provider calls behind this
/// seam; a webhook is verified <b>before</b> it is parsed, and parsing an event never mutates any state.</summary>
public interface IBillingGateway
{
    /// <summary>Whether the provider is configured and self-serve checkout/portal calls can be made. When false the
    /// endpoints surface a friendly "billing not yet available" state and never invoke the session calls below.</summary>
    bool IsConfigured { get; }

    /// <summary>Creates a hosted-checkout session for <paramref name="tenantId"/> subscribing to <paramref name="tier"/>,
    /// returning the URL to redirect the browser to. Never mutates the tenant's plan — the change lands only when the
    /// provider later posts a verified subscription webhook.</summary>
    /// <exception cref="BillingNotConfiguredException">The provider is not configured/wired (fail closed).</exception>
    Task<BillingSession> CreateCheckoutSessionAsync(string tenantId, BillingTierConfig tier, CancellationToken ct);

    /// <summary>Creates a hosted Billing-Portal session for <paramref name="tenantId"/> (manage payment method, invoices,
    /// plan), returning the URL to redirect to.</summary>
    /// <exception cref="BillingNotConfiguredException">The provider is not configured/wired (fail closed).</exception>
    Task<BillingSession> CreatePortalSessionAsync(string tenantId, CancellationToken ct);

    /// <summary>Verifies <paramref name="signatureHeader"/> against <paramref name="rawBody"/> and, only on success,
    /// parses the body into a <see cref="BillingWebhookEvent"/>. Returns false — writing no <paramref name="webhookEvent"/>
    /// the caller may act on — for a missing/invalid signature (fail closed) or a body that cannot be parsed, so the
    /// endpoint rejects it with a 400 and changes nothing. Verification happens strictly before parsing.</summary>
    bool TryReadWebhookEvent(string rawBody, string? signatureHeader, out BillingWebhookEvent webhookEvent);
}
