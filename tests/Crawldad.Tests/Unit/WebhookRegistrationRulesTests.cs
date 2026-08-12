using Crawldad.Web.Features.Webhooks;

namespace Crawldad.Tests.Unit;

/// <summary>The webhook name slug rule: a lowercase slug (alnum + hyphen, 1..64, no leading/trailing hyphen); a null or
/// empty name is rejected (the null guard the route never supplies, but the rule must still hold).</summary>
public class WebhookRegistrationRulesTests
{
    [Theory]
    [InlineData("prod", true)]
    [InlineData("a-b-c", true)]
    [InlineData("x9", true)]
    [InlineData("Bad_Name", false)]   // uppercase + underscore
    [InlineData("has space", false)]
    [InlineData("-lead", false)]      // leading hyphen
    [InlineData("trail-", false)]     // trailing hyphen
    [InlineData("", false)]           // empty
    [InlineData(null, false)]         // null (the guarded branch)
    public void Validates_name_slugs(string? name, bool valid) =>
        WebhookRegistrationRules.IsValidName(name).ShouldBe(valid);
}
