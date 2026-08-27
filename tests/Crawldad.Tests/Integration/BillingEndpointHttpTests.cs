using System.Text.Json;
using Alba;
using Crawldad.Tests.Support;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Tests.Integration;

/// <summary>The tenant-authed billing endpoints over the real Wolverine pipeline (fake gateway): config reports the
/// catalog + the tenant's current tier, checkout mints a URL for a self-serve tier (400 otherwise), and the portal mints
/// a URL. The unit tests cover every branch; these prove the routing, auth, and DI wiring.</summary>
[Collection(BillingApiCollection.Name)]
public sealed class BillingEndpointHttpTests(BillingApiFixture fixture)
{
    private IAlbaHost Host => fixture.Host;

    [Fact]
    public async Task Config_reports_the_catalog_and_configured_state()
    {
        var result = await Host.Scenario(x =>
        {
            x.Get.Url("/billing/config");
            x.StatusCodeShouldBeOk();
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        body.GetProperty("configured").GetBoolean().ShouldBeTrue();       // the fake gateway is always configured
        body.GetProperty("tiers").GetArrayLength().ShouldBeGreaterThan(2); // free/team/scale/enterprise
    }

    [Fact]
    public async Task Config_resolves_an_env_tenants_tier()
    {
        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(BillingApiFixture.TieredTenantKey));
            x.Get.Url("/billing/config");
            x.StatusCodeShouldBeOk();
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        body.GetProperty("currentTier").GetString().ShouldBe(BillingApiFixture.TieredTenantTier);
    }

    [Fact]
    public async Task Checkout_mints_a_url_for_a_self_serve_tier()
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(new { tier = "team" }).ToUrl("/billing/checkout-session");
            x.StatusCodeShouldBeOk();
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        body.GetProperty("url").GetString()!.ShouldContain("billing-result");
    }

    [Fact]
    public async Task Checkout_rejects_a_non_self_serve_tier() =>
        await Host.Scenario(x =>
        {
            x.Post.Json(new { tier = "free" }).ToUrl("/billing/checkout-session");
            x.StatusCodeShouldBe(StatusCodes.Status400BadRequest);
        });

    [Fact]
    public async Task Portal_mints_a_url()
    {
        var result = await Host.Scenario(x =>
        {
            x.Post.Json(new { }).ToUrl("/billing/portal-session");
            x.StatusCodeShouldBeOk();
        });

        var body = await result.ReadAsJsonAsync<JsonElement>();
        body.GetProperty("url").GetString()!.ShouldContain("outcome=portal");
    }
}
