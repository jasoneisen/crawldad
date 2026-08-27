using Crawldad.Api.Features.Billing;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>The tier catalog: the BUSINESS_MODEL.md defaults when config supplies none, a configured override when it
/// does, and the two lookups the slice needs — by moniker (case-insensitive) and by provider price id.</summary>
public class BillingCatalogTests
{
    private static BillingCatalog Catalog(BillingOptions options) => new(Options.Create(options));

    [Fact]
    public void Defaults_apply_when_no_tiers_are_configured()
    {
        var catalog = Catalog(new BillingOptions());

        catalog.Tiers.Count.ShouldBe(BillingTierCatalog.Defaults.Count);
        catalog.ByTier("team")!.Slots.ShouldBe(10);
        catalog.ByTier("scale")!.Slots.ShouldBe(50);
        catalog.ByTier("free")!.Slots.ShouldBe(2);
        catalog.ByTier("enterprise")!.Slots.ShouldBeNull();
        catalog.ByTier("team")!.SelfServe.ShouldBeTrue();
        catalog.ByTier("free")!.SelfServe.ShouldBeFalse();
    }

    [Fact]
    public void ByTier_is_case_insensitive_and_null_for_the_unknown()
    {
        var catalog = Catalog(new BillingOptions());

        catalog.ByTier("TEAM")!.Tier.ShouldBe("team");
        catalog.ByTier("nope").ShouldBeNull();
    }

    [Fact]
    public void ByPriceId_maps_a_known_price_and_is_null_otherwise()
    {
        var catalog = Catalog(new BillingOptions());

        catalog.ByPriceId("price_team")!.Tier.ShouldBe("team");
        catalog.ByPriceId("price_unknown").ShouldBeNull();
        catalog.ByPriceId(null).ShouldBeNull(); // a cancellation carries no price
    }

    [Fact]
    public void Configured_tiers_override_the_defaults()
    {
        var options = new BillingOptions
        {
            Tiers =
            [
                new BillingTierConfig { Tier = "starter", DisplayName = "Starter", PriceLabel = "$5", Slots = 3, SelfServe = true, PriceId = "price_starter" },
            ],
        };
        var catalog = Catalog(options);

        catalog.Tiers.Count.ShouldBe(1);
        catalog.ByTier("starter")!.Slots.ShouldBe(3);
        catalog.ByPriceId("price_starter")!.Tier.ShouldBe("starter");
        catalog.ByTier("team").ShouldBeNull(); // the defaults no longer apply
    }
}
