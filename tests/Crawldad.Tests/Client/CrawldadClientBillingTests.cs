using System.Net;
using Crawldad.Client;
using Crawldad.Contracts.Billing;

namespace Crawldad.Tests.Client;

/// <summary>The billing SDK partial over the stub transport: the three calls hit the right method/path with the tenant
/// key, deserialize their response, and map the typed error bodies (unknown tier → validation, unconfigured → 503).</summary>
public class CrawldadClientBillingTests
{
    [Fact]
    public async Task GetBillingConfig_reads_the_config_endpoint()
    {
        var config = new BillingConfigResponse(true, "team",
        [
            new BillingTierOption("free", "Free", "$0", 2, false, false),
            new BillingTierOption("team", "Team", "$99/mo", 10, true, true),
        ]);
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(config));
        var client = ClientTestHarness.ClientFor(handler);

        var result = await client.GetBillingConfigAsync();

        result.Configured.ShouldBeTrue();
        result.CurrentTier.ShouldBe("team");
        result.Tiers.Count.ShouldBe(2);
        handler.Last.Method.ShouldBe(HttpMethod.Get);
        handler.Last.Path.ShouldBe("/billing/config");
        handler.Last.Authorization.ShouldBe($"Bearer {ClientTestHarness.ApiKey}");
    }

    [Fact]
    public async Task CreateCheckoutSession_posts_the_target_tier_and_returns_the_url()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new BillingSessionResponse("https://checkout.example/cs_test_1")));
        var client = ClientTestHarness.ClientFor(handler);

        var result = await client.CreateCheckoutSessionAsync("team");

        result.Url.ShouldBe("https://checkout.example/cs_test_1");
        handler.Last.Method.ShouldBe(HttpMethod.Post);
        handler.Last.Path.ShouldBe("/billing/checkout-session");
        handler.Last.Body.ShouldContain("team");
        handler.Last.Authorization.ShouldBe($"Bearer {ClientTestHarness.ApiKey}");
    }

    [Fact]
    public async Task CreatePortalSession_posts_with_no_body_and_returns_the_url()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new BillingSessionResponse("https://portal.example/ps_test_1")));
        var client = ClientTestHarness.ClientFor(handler);

        var result = await client.CreatePortalSessionAsync();

        result.Url.ShouldBe("https://portal.example/ps_test_1");
        handler.Last.Method.ShouldBe(HttpMethod.Post);
        handler.Last.Path.ShouldBe("/billing/portal-session");
        handler.Last.Body.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unpurchasable_tier_maps_to_a_validation_exception()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.JsonRaw(HttpStatusCode.BadRequest, """{"errors":{"tier":["tier 'gold' is not a purchasable plan"]}}"""));
        var client = ClientTestHarness.ClientFor(handler);

        var ex = await Should.ThrowAsync<CrawldadValidationException>(() => client.CreateCheckoutSessionAsync("gold"));

        ex.StatusCode.ShouldBe(400);
        ex.Errors["tier"][0].ShouldContain("not a purchasable plan");
    }

    [Fact]
    public async Task An_unconfigured_provider_maps_to_a_503_api_exception()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.JsonRaw(HttpStatusCode.ServiceUnavailable, """{"title":"billing_not_configured","status":503,"detail":"Billing is not yet available for this deployment."}"""));
        var client = ClientTestHarness.ClientFor(handler);

        var ex = await Should.ThrowAsync<CrawldadApiException>(() => client.CreatePortalSessionAsync());

        ex.StatusCode.ShouldBe(503);
        ex.Message.ShouldContain("not yet available");
    }

    [Fact]
    public async Task Checkout_rejects_a_blank_tier_before_any_request()
    {
        var handler = new StubHttpMessageHandler(_ => ClientTestHarness.Json(new BillingSessionResponse("unused")));
        var client = ClientTestHarness.ClientFor(handler);

        await Should.ThrowAsync<ArgumentException>(() => client.CreateCheckoutSessionAsync(""));
        handler.Requests.ShouldBeEmpty();
    }
}
