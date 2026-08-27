using Crawldad.Api.Features.Billing;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>The production Stripe gateway is a fail-closed stub (no Stripe SDK yet): <see cref="StripeBillingGateway.IsConfigured"/>
/// reflects credential presence, session calls throw a clear <see cref="BillingNotConfiguredException"/> (with a message
/// that distinguishes "no credentials" from "credentials present but SDK not wired"), and the webhook verifier rejects
/// every event.</summary>
public class StripeBillingGatewayTests
{
    private static readonly BillingTierConfig _team = new() { Tier = "team", DisplayName = "Team", PriceLabel = "$99/mo", Slots = 10, SelfServe = true, PriceId = "price_team" };

    private static StripeBillingGateway Gateway(bool configured) => new(Options.Create(new BillingOptions
    {
        Stripe = configured ? new BillingStripeOptions { SecretKey = "sk_test_x", WebhookSecret = "whsec_x" } : new BillingStripeOptions(),
    }));

    [Fact]
    public void Is_not_configured_without_both_credentials() => Gateway(configured: false).IsConfigured.ShouldBeFalse();

    [Fact]
    public void Is_configured_with_both_credentials() => Gateway(configured: true).IsConfigured.ShouldBeTrue();

    [Fact]
    public async Task Unconfigured_session_calls_fail_closed_with_a_no_credentials_message()
    {
        var gateway = Gateway(configured: false);

        var checkout = await Should.ThrowAsync<BillingNotConfiguredException>(() => gateway.CreateCheckoutSessionAsync("acme", _team, CancellationToken.None));
        checkout.Message.ShouldContain("not configured");

        var portal = await Should.ThrowAsync<BillingNotConfiguredException>(() => gateway.CreatePortalSessionAsync("acme", CancellationToken.None));
        portal.Message.ShouldContain("not configured");
    }

    [Fact]
    public async Task Configured_session_calls_still_fail_closed_but_flag_the_unwired_sdk()
    {
        var gateway = Gateway(configured: true);

        var checkout = await Should.ThrowAsync<BillingNotConfiguredException>(() => gateway.CreateCheckoutSessionAsync("acme", _team, CancellationToken.None));
        checkout.Message.ShouldContain("not yet wired");

        await Should.ThrowAsync<BillingNotConfiguredException>(() => gateway.CreatePortalSessionAsync("acme", CancellationToken.None));
    }

    [Fact]
    public void The_webhook_verifier_rejects_every_event()
    {
        Gateway(configured: true).TryReadWebhookEvent("""{"id":"e","type":"customer.subscription.updated","tenant":"acme"}""", "sig", out var webhookEvent).ShouldBeFalse();
        webhookEvent.ShouldBeNull();
    }
}
