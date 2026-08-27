using Crawldad.Api.Features.Tenancy;

namespace Crawldad.Tests.Unit;

/// <summary>The registry-tenant field guards enforced by the create endpoint: the id slug (which excludes <c>':'</c> so
/// the per-tenant secret-vault namespace stays unambiguous), the display name, the tier, and the slot allowance.</summary>
public class TenantRulesTests
{
    [Theory]
    [InlineData("acme")]
    [InlineData("acme-corp")]
    [InlineData("t1")]
    [InlineData("a0b1c2")]
    public void Accepts_a_valid_id_slug(string id) => TenantRules.IsValidId(id).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Acme")]          // uppercase
    [InlineData("acme:evil")]     // ':' would break the Secrets:{tenant}:{ref} namespace
    [InlineData("-acme")]         // leading hyphen
    [InlineData("acme-")]         // trailing hyphen
    [InlineData("acme corp")]     // space
    public void Rejects_an_invalid_id_slug(string? id) => TenantRules.IsValidId(id).ShouldBeFalse();

    [Fact]
    public void Rejects_an_id_longer_than_the_slug_bound() =>
        TenantRules.IsValidId(new string('a', 65)).ShouldBeFalse();

    [Fact]
    public void Accepts_a_present_display_name() => TenantRules.IsValidDisplayName("Acme Corp").ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_a_missing_display_name(string? name) => TenantRules.IsValidDisplayName(name).ShouldBeFalse();

    [Fact]
    public void Rejects_a_display_name_over_the_length_bound() =>
        TenantRules.IsValidDisplayName(new string('x', TenantRules.MaxDisplayNameLength + 1)).ShouldBeFalse();

    [Fact]
    public void Accepts_an_absent_tier() => TenantRules.IsValidTier(null).ShouldBeTrue();

    [Fact]
    public void Rejects_a_tier_over_the_length_bound() =>
        TenantRules.IsValidTier(new string('x', TenantRules.MaxTierLength + 1)).ShouldBeFalse();

    [Fact]
    public void Accepts_an_absent_or_positive_slot_allowance()
    {
        TenantRules.IsValidSlotAllowance(null).ShouldBeTrue();
        TenantRules.IsValidSlotAllowance(1).ShouldBeTrue();
        TenantRules.IsValidSlotAllowance(32).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void Rejects_a_non_positive_slot_allowance(int allowance) => TenantRules.IsValidSlotAllowance(allowance).ShouldBeFalse();
}
