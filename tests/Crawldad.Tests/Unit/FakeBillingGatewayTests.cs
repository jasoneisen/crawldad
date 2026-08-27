using Crawldad.Api.Features.Billing;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>The development/test fake gateway: always configured; session calls return in-app result URLs (relative when
/// no portal base is set, absolute when it is); and the webhook verifier is a shared-token check applied BEFORE the body
/// is parsed, with every "cannot act" parse case (bad token, malformed/blank/non-object body, missing field, unknown
/// type) returning false so the endpoint rejects it.</summary>
public class FakeBillingGatewayTests
{
    private static readonly BillingTierConfig _team = new() { Tier = "team", DisplayName = "Team", PriceLabel = "$99/mo", Slots = 10, SelfServe = true, PriceId = "price_team" };

    private static FakeBillingGateway Gateway(string portalReturnUrl = "", string webhookSecret = "") =>
        new(Options.Create(new BillingOptions { PortalReturnUrl = portalReturnUrl, Stripe = new BillingStripeOptions { WebhookSecret = webhookSecret } }));

    private static string Body(string id, string type, string tenant, string? priceId = "price_team") =>
        priceId is null
            ? $$"""{"id":"{{id}}","type":"{{type}}","tenant":"{{tenant}}"}"""
            : $$"""{"id":"{{id}}","type":"{{type}}","tenant":"{{tenant}}","priceId":"{{priceId}}"}""";

    [Fact]
    public void Is_always_configured() => Gateway().IsConfigured.ShouldBeTrue();

    [Fact]
    public async Task Checkout_session_is_a_relative_result_url_carrying_the_tier()
    {
        var session = await Gateway().CreateCheckoutSessionAsync("acme", _team, CancellationToken.None);

        session.Url.ShouldStartWith(FakeBillingGateway.ResultPath);
        session.Url.ShouldContain("outcome=checkout");
        session.Url.ShouldContain("tier=team");
    }

    [Fact]
    public async Task Checkout_session_is_absolute_when_a_portal_base_is_configured()
    {
        var session = await Gateway(portalReturnUrl: "https://app.crawldad.test/").CreateCheckoutSessionAsync("acme", _team, CancellationToken.None);

        session.Url.ShouldStartWith("https://app.crawldad.test/app/account/billing-result");
    }

    [Fact]
    public async Task Portal_session_is_a_result_url()
    {
        var session = await Gateway().CreatePortalSessionAsync("acme", CancellationToken.None);

        session.Url.ShouldContain("outcome=portal");
    }

    [Theory]
    [InlineData("customer.subscription.created", BillingSubscriptionChange.Created)]
    [InlineData("customer.subscription.updated", BillingSubscriptionChange.Updated)]
    [InlineData("customer.subscription.deleted", BillingSubscriptionChange.Cancelled)]
    public void A_valid_signed_event_parses_with_the_normalized_change(string type, BillingSubscriptionChange expected)
    {
        var ok = Gateway().TryReadWebhookEvent(Body("evt_1", type, "acme"), FakeBillingGateway.DefaultSignature, out var webhookEvent);

        ok.ShouldBeTrue();
        webhookEvent.EventId.ShouldBe("evt_1");
        webhookEvent.Change.ShouldBe(expected);
        webhookEvent.TenantId.ShouldBe("acme");
        webhookEvent.PriceId.ShouldBe("price_team");
    }

    [Fact]
    public void A_configured_secret_is_the_expected_signature()
    {
        var gateway = Gateway(webhookSecret: "whsec_fake_1");

        gateway.TryReadWebhookEvent(Body("evt_1", "customer.subscription.updated", "acme"), "whsec_fake_1", out _).ShouldBeTrue();
        gateway.TryReadWebhookEvent(Body("evt_1", "customer.subscription.updated", "acme"), FakeBillingGateway.DefaultSignature, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_cancellation_may_carry_no_price()
    {
        var ok = Gateway().TryReadWebhookEvent(Body("evt_1", "customer.subscription.deleted", "acme", priceId: null), FakeBillingGateway.DefaultSignature, out var webhookEvent);

        ok.ShouldBeTrue();
        webhookEvent.PriceId.ShouldBeNull();
    }

    [Theory]
    [InlineData("wrong-signature", """{"id":"e","type":"customer.subscription.updated","tenant":"acme"}""")] // bad token → verify fails before parse
    [InlineData(FakeBillingGateway.DefaultSignature, "")]                                                       // blank body
    [InlineData(FakeBillingGateway.DefaultSignature, "{ not json")]                                             // malformed JSON
    [InlineData(FakeBillingGateway.DefaultSignature, "[]")]                                                     // not an object
    [InlineData(FakeBillingGateway.DefaultSignature, """{"type":"customer.subscription.updated","tenant":"a"}""")] // missing id
    [InlineData(FakeBillingGateway.DefaultSignature, """{"id":123,"type":"customer.subscription.updated","tenant":"a"}""")] // id wrong-kinded
    [InlineData(FakeBillingGateway.DefaultSignature, """{"id":"","type":"customer.subscription.updated","tenant":"a"}""")] // id empty
    [InlineData(FakeBillingGateway.DefaultSignature, """{"id":"e","tenant":"a"}""")]                            // missing type
    [InlineData(FakeBillingGateway.DefaultSignature, """{"id":"e","type":"invoice.paid","tenant":"a"}""")]      // unhandled type
    [InlineData(FakeBillingGateway.DefaultSignature, """{"id":"e","type":"customer.subscription.updated"}""")]  // missing tenant
    public void A_bad_signature_or_unparseable_event_returns_false(string signature, string body)
    {
        Gateway().TryReadWebhookEvent(body, signature, out var webhookEvent).ShouldBeFalse();
        webhookEvent.ShouldBeNull();
    }
}
