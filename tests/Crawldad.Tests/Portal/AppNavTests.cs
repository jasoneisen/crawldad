using Bunit;
using Crawldad.Portal.Components.Layout;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the authenticated left navigation.</summary>
public class AppNavTests : BunitContext
{
    [Fact]
    public void Renders_the_six_sections_in_order()
    {
        var cut = Render<AppNav>();

        var hrefs = cut.FindAll("a.nav-link").Select(a => a.GetAttribute("href")).ToArray();

        hrefs.ShouldBe(["/app/runs", "/app/live", "/app/payloads", "/app/webhooks", "/app/usage", "/app/account"]);
        cut.Markup.ShouldContain("Runs");
        cut.Markup.ShouldContain("Webhooks");
        cut.Markup.ShouldContain("Account");
    }
}
