using Crawldad.Api.Features.Tenancy;

namespace Crawldad.Tests.Unit;

/// <summary>Unit cover for <see cref="TenantKeyRules.NormalizeLabel"/> — the optional self-service key label guard
/// (absent/blank → unlabelled, trimmed, length-bounded).</summary>
public class TenantKeyRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_label_is_an_unlabelled_key(string? label)
    {
        var (normalized, error) = TenantKeyRules.NormalizeLabel(label);
        normalized.ShouldBeNull();
        error.ShouldBeNull();
    }

    [Fact]
    public void A_valid_label_is_trimmed_and_accepted()
    {
        var (normalized, error) = TenantKeyRules.NormalizeLabel("  ci-github  ");
        normalized.ShouldBe("ci-github");
        error.ShouldBeNull();
    }

    [Fact]
    public void A_label_at_the_maximum_length_is_accepted()
    {
        var atMax = new string('x', TenantKeyRules.MaxLabelLength);
        var (normalized, error) = TenantKeyRules.NormalizeLabel(atMax);
        normalized.ShouldBe(atMax);
        error.ShouldBeNull();
    }

    [Fact]
    public void A_too_long_label_is_rejected()
    {
        var (normalized, error) = TenantKeyRules.NormalizeLabel(new string('x', TenantKeyRules.MaxLabelLength + 1));
        normalized.ShouldBeNull();
        error.ShouldNotBeNull();
        error.ShouldContain(TenantKeyRules.MaxLabelLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
