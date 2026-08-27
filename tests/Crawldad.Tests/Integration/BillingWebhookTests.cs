using Alba;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>The public, signature-verified billing webhook end to end (fake gateway): a verified subscription event moves
/// a registry tenant's tier + slots; a bad signature or an unknown price / tenant changes nothing; a replayed event id is
/// a no-op; and a cancellation downgrades to free. The tenant in the payload is authoritative — no tenant key is
/// presented.</summary>
[Collection(BillingApiCollection.Name)]
public sealed class BillingWebhookTests(BillingApiFixture fixture) : IAsyncLifetime
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    private IAlbaHost Host => fixture.Host;

    private ITenantRegistryStore Registry => Host.Services.GetRequiredService<ITenantRegistryStore>();

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_verified_subscription_update_applies_the_mapped_tier_and_slots()
    {
        var id = await SeedTenantAsync(tier: "free", slots: 2);

        await PostWebhookAsync(new { id = "evt_1", type = "customer.subscription.updated", tenant = id, priceId = "price_team" },
            BillingApiFixture.WebhookSecret, StatusCodes.Status200OK);

        var tenant = await Registry.FindAsync(id, _ct);
        tenant!.Tier.ShouldBe("team");
        tenant.SlotAllowance.ShouldBe(10);
    }

    [Fact]
    public async Task A_cancellation_downgrades_to_free()
    {
        var id = await SeedTenantAsync(tier: "team", slots: 10);

        await PostWebhookAsync(new { id = "evt_cancel", type = "customer.subscription.deleted", tenant = id },
            BillingApiFixture.WebhookSecret, StatusCodes.Status200OK);

        var tenant = await Registry.FindAsync(id, _ct);
        tenant!.Tier.ShouldBe("free");
        tenant.SlotAllowance.ShouldBe(2);
    }

    [Fact]
    public async Task A_bad_signature_is_rejected_and_changes_nothing()
    {
        var id = await SeedTenantAsync(tier: "free", slots: 2);

        await PostWebhookAsync(new { id = "evt_2", type = "customer.subscription.updated", tenant = id, priceId = "price_team" },
            "not-the-secret", StatusCodes.Status400BadRequest);

        var tenant = await Registry.FindAsync(id, _ct);
        tenant!.Tier.ShouldBe("free");
        tenant.SlotAllowance.ShouldBe(2);
    }

    [Fact]
    public async Task An_event_for_a_tenant_not_in_the_registry_is_dropped()
    {
        // tenant-alpha is an env-configured tenant, not a registry tenant → read-only for billing → dropped (200, not 500).
        await PostWebhookAsync(new { id = "evt_3", type = "customer.subscription.updated", tenant = TestTenants.PrimaryId, priceId = "price_team" },
            BillingApiFixture.WebhookSecret, StatusCodes.Status200OK);

        (await Registry.FindAsync(TestTenants.PrimaryId, _ct)).ShouldBeNull();
    }

    [Fact]
    public async Task A_price_that_maps_to_no_tier_is_dropped()
    {
        var id = await SeedTenantAsync(tier: "free", slots: 2);

        await PostWebhookAsync(new { id = "evt_4", type = "customer.subscription.updated", tenant = id, priceId = "price_unknown" },
            BillingApiFixture.WebhookSecret, StatusCodes.Status200OK);

        var tenant = await Registry.FindAsync(id, _ct);
        tenant!.Tier.ShouldBe("free");
        tenant.SlotAllowance.ShouldBe(2);
    }

    [Fact]
    public async Task A_replayed_event_id_is_a_no_op()
    {
        var id = await SeedTenantAsync(tier: "free", slots: 2);

        // First delivery applies team.
        await PostWebhookAsync(new { id = "evt_dup", type = "customer.subscription.updated", tenant = id, priceId = "price_team" },
            BillingApiFixture.WebhookSecret, StatusCodes.Status200OK);

        // A redelivery with the SAME event id but a DIFFERENT price must be de-duplicated — the tier stays team, not scale.
        await PostWebhookAsync(new { id = "evt_dup", type = "customer.subscription.updated", tenant = id, priceId = "price_scale" },
            BillingApiFixture.WebhookSecret, StatusCodes.Status200OK);

        var tenant = await Registry.FindAsync(id, _ct);
        tenant!.Tier.ShouldBe("team");
        tenant.SlotAllowance.ShouldBe(10);
    }

    [Fact]
    public async Task SetPlanAsync_returns_null_for_an_unknown_tenant() =>
        (await Registry.SetPlanAsync("no-such-tenant", "team", 10, DateTimeOffset.UnixEpoch, _ct)).ShouldBeNull();

    private async Task<string> SeedTenantAsync(string tier, int? slots)
    {
        var id = "wh-" + Guid.NewGuid().ToString("N");
        await Registry.CreateAsync(new RegistryTenant
        {
            Id = id,
            DisplayName = id,
            Actor = id,
            Tier = tier,
            SlotAllowance = slots,
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        }, _ct);
        return id;
    }

    private async Task PostWebhookAsync(object body, string signature, int expectedStatus) =>
        await Host.Scenario(x =>
        {
            x.RemoveRequestHeader("Authorization"); // the webhook is anonymous — present no tenant key
            x.WithRequestHeader("Stripe-Signature", signature);
            x.Post.Json(body).ToUrl("/billing/webhook");
            x.StatusCodeShouldBe(expectedStatus);
        });
}
