using Bunit;
using Crawldad.Portal.Components.Layout;
using Crawldad.Portal.Components.Pages;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the marketing layout and the placeholder home page.</summary>
public class LayoutTests : BunitContext
{
    [Fact]
    public void Marketing_layout_wraps_its_body_in_the_page_shell()
    {
        var cut = Render<MarketingLayout>(ps => ps.Add(l => l.Body, "<p>hello-body</p>"));

        cut.Find(".page").ShouldNotBeNull();
        cut.Markup.ShouldContain("hello-body");
    }

    [Fact]
    public void Home_shows_the_product_name_and_links_to_login()
    {
        var cut = Render<Home>();

        // The landing page's precise content is asserted in MarketingHomeTests; this is a layout-level smoke check.
        cut.Markup.ShouldContain("Crawldad");
        cut.FindAll("a[href=\"/login\"]").Count.ShouldBeGreaterThan(0);
    }
}
