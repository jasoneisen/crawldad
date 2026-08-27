using System.Security.Claims;
using System.Text.Json;
using Crawldad.Api.Features.Billing;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Billing;
using Crawldad.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>The tenant-authed billing endpoints (checkout / portal / config) called directly: the fail-closed 503 when
/// unconfigured (both the up-front IsConfigured guard and the configured-but-throwing catch), the 400 for an unknown or
/// non-self-serve checkout tier, the happy URL, and the config endpoint's current-tier resolution across the registry /
/// env / none sources plus its IsCurrent + Configured flags.</summary>
public class BillingEndpointsTests
{
    // Executing an IResult writes through RequestServices (an ILoggerFactory, and JSON options with a default fallback).
    private static readonly IServiceProvider _services = new ServiceCollection().AddLogging().BuildServiceProvider();
    private static readonly JsonSerializerOptions _web = new(JsonSerializerDefaults.Web);

    private static BillingCatalog Catalog() => new(Options.Create(new BillingOptions()));

    private static FakeBillingGateway ConfiguredFake() => new(Options.Create(new BillingOptions()));

    private static StripeBillingGateway UnconfiguredStub() => new(Options.Create(new BillingOptions()));

    private static TenantContext TenantFor(string tenantId)
    {
        var identity = new ClaimsIdentity([new Claim(CrawldadClaims.TenantId, tenantId), new Claim(CrawldadClaims.Actor, tenantId)], "test");
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        return new TenantContext(new HttpContextAccessor { HttpContext = http });
    }

    private static async Task<(int Status, string Body)> RunAsync(IResult result)
    {
        var http = new DefaultHttpContext { RequestServices = _services };
        using var body = new MemoryStream();
        http.Response.Body = body;
        await result.ExecuteAsync(http);
        body.Position = 0;
        return (http.Response.StatusCode, await new StreamReader(body).ReadToEndAsync());
    }

    // ---- checkout ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Checkout_returns_a_url_for_a_self_serve_tier()
    {
        var result = await CheckoutSessionEndpoint.Handle(new CheckoutSessionRequest("team"), TenantFor("acme"), ConfiguredFake(), Catalog(), CancellationToken.None);

        var (status, body) = await RunAsync(result);
        status.ShouldBe(StatusCodes.Status200OK);
        body.ShouldContain("billing-result");
    }

    [Theory]
    [InlineData("gold")]  // unknown
    [InlineData("free")]  // known but not self-serve
    [InlineData("")]      // blank
    public async Task Checkout_rejects_an_unpurchasable_tier(string tier)
    {
        var result = await CheckoutSessionEndpoint.Handle(new CheckoutSessionRequest(tier), TenantFor("acme"), ConfiguredFake(), Catalog(), CancellationToken.None);

        (await RunAsync(result)).Status.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Checkout_is_503_when_the_provider_is_unconfigured()
    {
        var result = await CheckoutSessionEndpoint.Handle(new CheckoutSessionRequest("team"), TenantFor("acme"), UnconfiguredStub(), Catalog(), CancellationToken.None);

        (await RunAsync(result)).Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Checkout_is_503_when_a_configured_gateway_still_throws_not_configured()
    {
        var result = await CheckoutSessionEndpoint.Handle(new CheckoutSessionRequest("team"), TenantFor("acme"), new ThrowingGateway(), Catalog(), CancellationToken.None);

        (await RunAsync(result)).Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    // ---- portal --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Portal_returns_a_url_when_configured()
    {
        var result = await PortalSessionEndpoint.Handle(TenantFor("acme"), ConfiguredFake(), CancellationToken.None);

        var (status, body) = await RunAsync(result);
        status.ShouldBe(StatusCodes.Status200OK);
        body.ShouldContain("outcome=portal");
    }

    [Fact]
    public async Task Portal_is_503_when_the_provider_is_unconfigured()
    {
        var result = await PortalSessionEndpoint.Handle(TenantFor("acme"), UnconfiguredStub(), CancellationToken.None);

        (await RunAsync(result)).Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task Portal_is_503_when_a_configured_gateway_still_throws_not_configured()
    {
        var result = await PortalSessionEndpoint.Handle(TenantFor("acme"), new ThrowingGateway(), CancellationToken.None);

        (await RunAsync(result)).Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    // ---- config --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Config_reports_configured_and_the_registry_tenants_current_tier()
    {
        var store = new FakeTenantRegistryStore();
        store.AddActiveTenantKey("acme", tier: "team");

        var config = await ConfigAsync(TenantFor("acme"), ConfiguredFake(), store, new TenantOptions());

        config.Configured.ShouldBeTrue();
        config.CurrentTier.ShouldBe("team");
        config.Tiers.Single(t => string.Equals(t.Tier, "team", StringComparison.Ordinal)).IsCurrent.ShouldBeTrue();
        config.Tiers.Single(t => string.Equals(t.Tier, "scale", StringComparison.Ordinal)).IsCurrent.ShouldBeFalse();
    }

    [Fact]
    public async Task Config_falls_back_to_the_env_tenants_tier_when_not_in_the_registry()
    {
        var tenants = new TenantOptions { Tenants = { new TenantDescriptor { Id = "acme", Actor = "a", ApiKey = "0123456789abcdef", Tier = "scale" } } };

        var config = await ConfigAsync(TenantFor("acme"), ConfiguredFake(), new FakeTenantRegistryStore(), tenants);

        config.CurrentTier.ShouldBe("scale");
    }

    [Fact]
    public async Task Config_reports_no_current_tier_when_neither_source_has_one()
    {
        var tenants = new TenantOptions { Tenants = { new TenantDescriptor { Id = "acme", Actor = "a", ApiKey = "0123456789abcdef" } } };

        var config = await ConfigAsync(TenantFor("acme"), ConfiguredFake(), new FakeTenantRegistryStore(), tenants);

        config.CurrentTier.ShouldBeNull();
    }

    [Fact]
    public async Task Config_reports_not_configured_for_the_production_stub()
    {
        var config = await ConfigAsync(TenantFor("acme"), UnconfiguredStub(), new FakeTenantRegistryStore(), new TenantOptions());

        config.Configured.ShouldBeFalse();
    }

    private static async Task<BillingConfigResponse> ConfigAsync(TenantContext tenant, IBillingGateway gateway, FakeTenantRegistryStore store, TenantOptions tenants)
    {
        var result = await BillingConfigEndpoint.Handle(tenant, gateway, Catalog(), store, Options.Create(tenants), CancellationToken.None);
        var (status, body) = await RunAsync(result);
        status.ShouldBe(StatusCodes.Status200OK);
        return JsonSerializer.Deserialize<BillingConfigResponse>(body, _web)!;
    }

    private sealed class ThrowingGateway : IBillingGateway
    {
        public bool IsConfigured => true;

        public Task<BillingSession> CreateCheckoutSessionAsync(string tenantId, BillingTierConfig tier, CancellationToken ct) =>
            throw new BillingNotConfiguredException("configured but the SDK is not wired");

        public Task<BillingSession> CreatePortalSessionAsync(string tenantId, CancellationToken ct) =>
            throw new BillingNotConfiguredException("configured but the SDK is not wired");

        public bool TryReadWebhookEvent(string rawBody, string? signatureHeader, out BillingWebhookEvent webhookEvent)
        {
            webhookEvent = null!;
            return false;
        }
    }
}
