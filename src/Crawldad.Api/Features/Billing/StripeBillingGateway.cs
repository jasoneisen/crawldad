using Microsoft.Extensions.Options;

namespace Crawldad.Api.Features.Billing;

/// <summary>The production <see cref="IBillingGateway"/> — a deliberate, fail-closed <b>stub</b>. The Stripe .NET SDK is
/// intentionally NOT a dependency yet (this is billing scaffolding), so no method can talk to Stripe: session calls throw
/// a clear <see cref="BillingNotConfiguredException"/> and the webhook verifier returns false (rejecting every event). The
/// endpoints translate both into a friendly, never-500 "billing not yet available" state. <see cref="IsConfigured"/>
/// reflects whether the credentials exist, so an operator can see whether config is in place even while the integration
/// is stubbed. Each numbered comment marks where the real Stripe call plugs in.</summary>
internal sealed class StripeBillingGateway(IOptions<BillingOptions> options) : IBillingGateway
{
    private readonly BillingStripeOptions _stripe = options.Value.Stripe;

    /// <summary>Whether both Stripe credentials are configured. True does not yet imply a working integration (the SDK is
    /// unwired) — it is the config-presence signal the fail-closed messages below key off.</summary>
    public bool IsConfigured => _stripe.IsConfigured;

    // 1) Real impl: new Stripe.Checkout.SessionService(new StripeClient(_stripe.SecretKey)).CreateAsync(new SessionCreateOptions
    //    { Mode = "subscription", LineItems = [ { Price = tier.PriceId, Quantity = 1 } ], ClientReferenceId = tenantId,
    //      SubscriptionData = { Metadata = { ["tenant"] = tenantId } }, SuccessUrl = …, CancelUrl = … }) → return session.Url.
    public Task<BillingSession> CreateCheckoutSessionAsync(string tenantId, BillingTierConfig tier, CancellationToken ct) =>
        throw NotConfigured();

    // 2) Real impl: new Stripe.BillingPortal.SessionService(client).CreateAsync(new SessionCreateOptions
    //    { Customer = <resolved stripe customer id for tenantId>, ReturnUrl = … }) → return session.Url.
    public Task<BillingSession> CreatePortalSessionAsync(string tenantId, CancellationToken ct) =>
        throw NotConfigured();

    // 3) Real impl: EventUtility.ConstructEvent(rawBody, signatureHeader, _stripe.WebhookSecret) (throws on a bad
    //    signature → return false), then read the Subscription, its price id, and the tenant id from metadata into a
    //    BillingWebhookEvent. Until then every event is rejected — fail closed, never a spoofed plan change.
    public bool TryReadWebhookEvent(string rawBody, string? signatureHeader, out BillingWebhookEvent webhookEvent)
    {
        webhookEvent = null!;
        return false;
    }

    // A clear, secret-free message that distinguishes "no credentials" from "credentials present but SDK not yet wired",
    // so an operator sees exactly why billing is unavailable. Never includes any credential value.
    private BillingNotConfiguredException NotConfigured() => new(IsConfigured
        ? "Stripe billing credentials are configured, but the Stripe SDK integration is not yet wired (scaffolding stub)."
        : "Stripe billing is not configured (no Billing:Stripe:SecretKey / WebhookSecret).");
}
