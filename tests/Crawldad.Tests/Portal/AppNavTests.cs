using Bunit;
using Crawldad.Portal.Components.Layout;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the authenticated left navigation.</summary>
public class AppNavTests : BunitContext
{
    [Fact]
    public void Renders_the_five_sections_in_order()
    {
        var cut = Render<AppNav>();

        var hrefs = cut.FindAll("a.nav-link").Select(a => a.GetAttribute("href")).ToArray();

        hrefs.ShouldBe(["/app/runs", "/app/live", "/app/payloads", "/app/webhooks", "/app/account"]);
    }

    [Fact]
    public void Labels_each_section()
    {
        var cut = Render<AppNav>();

        var titles = cut.FindAll(".nav-link-title").Select(e => e.TextContent.Trim()).ToArray();

        titles.ShouldBe(["Runs", "Live", "Payloads", "Webhooks", "Account"]);
    }
}
