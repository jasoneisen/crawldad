using System.Net;
using Crawldad.Client;
using Crawldad.Contracts.Tenancy;

namespace Crawldad.Tests.Client;

/// <summary>Unit tests for the tenancy surface over a stub handler: <c>GET /tenant</c> and <c>GET /usage</c> send the
/// bearer key to the right path and round-trip their <c>Crawldad.Contracts</c> shapes (including the optional tier and
/// the nested usage records), and a rejected key surfaces as the typed unauthorized exception the account-link flow
/// depends on.</summary>
public class CrawldadClientTenancyTests
{
    [Fact]
    public async Task GetTenant_sends_a_bearer_get_to_tenant_and_maps_the_profile()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new TenantProfileResponse("tenant-alpha", "alpha@crawldad.test", "pro", 5, 20)));
        var client = ClientTestHarness.ClientFor(handler);

        var profile = await client.GetTenantAsync();

        profile.TenantId.ShouldBe("tenant-alpha");
        profile.DisplayName.ShouldBe("alpha@crawldad.test");
        profile.Tier.ShouldBe("pro");
        profile.SlotAllowance.ShouldBe(5);
        profile.QueueDepthAllowance.ShouldBe(20);

        handler.Last.Method.ShouldBe(HttpMethod.Get);
        handler.Last.Path.ShouldBe("/tenant");
        handler.Last.Authorization.ShouldBe($"Bearer {ClientTestHarness.ApiKey}");
    }

    [Fact]
    public async Task GetTenant_maps_an_omitted_tier_as_null()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new TenantProfileResponse("tenant-free", "free@crawldad.test", Tier: null, 1, 5)));
        var client = ClientTestHarness.ClientFor(handler);

        var profile = await client.GetTenantAsync();

        profile.Tier.ShouldBeNull();
    }

    [Fact]
    public async Task GetTenant_maps_a_rejected_key_to_the_unauthorized_exception()
    {
        var handler = ClientTestHarness.Always(() => ClientTestHarness.Empty(HttpStatusCode.Unauthorized));
        var client = ClientTestHarness.ClientFor(handler);

        var ex = await Should.ThrowAsync<CrawldadUnauthorizedException>(() => client.GetTenantAsync());

        ex.StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task GetUsage_sends_a_bearer_get_to_usage_and_maps_the_nested_snapshot()
    {
        var handler = new StubHttpMessageHandler(_ =>
            ClientTestHarness.Json(new UsageResponse(
                new UsageSlots(2, 5),
                new UsageQueueStats(3, 37, 1200),
                412,
                new UsageEvents(5000, 100, 84, 611))));
        var client = ClientTestHarness.ClientFor(handler);

        var usage = await client.GetUsageAsync();

        usage.Slots.InUse.ShouldBe(2);
        usage.Slots.Allowance.ShouldBe(5);
        usage.Queue.Depth.ShouldBe(3);
        usage.Queue.Sampled.ShouldBe(37);
        usage.Queue.P95WaitMs.ShouldBe(1200);
        usage.RunsStartedThisMonth.ShouldBe(412);
        usage.Events.Guardrail.ShouldBe(5000);
        usage.Events.Sampled.ShouldBe(100);
        usage.Events.Avg.ShouldBe(84);
        usage.Events.Max.ShouldBe(611);

        handler.Last.Method.ShouldBe(HttpMethod.Get);
        handler.Last.Path.ShouldBe("/usage");
        handler.Last.Authorization.ShouldBe($"Bearer {ClientTestHarness.ApiKey}");
    }

    [Fact]
    public async Task GetUsage_maps_a_rejected_key_to_the_unauthorized_exception()
    {
        var handler = ClientTestHarness.Always(() => ClientTestHarness.Empty(HttpStatusCode.Unauthorized));
        var client = ClientTestHarness.ClientFor(handler);

        await Should.ThrowAsync<CrawldadUnauthorizedException>(() => client.GetUsageAsync());
    }
}
