using Bunit;
using Crawldad.Portal.Auth;
using Crawldad.Portal.Components.Layout;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the authenticated app shell: the vertical nav, the usage-indicator placeholder,
/// the signed-in email, and the antiforgery-guarded sign-out form.</summary>
public class AppLayoutTests : BunitContext
{
    public AppLayoutTests() =>
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());

    private IRenderedComponent<AppLayout> RenderShell(string email, string body = "<p>body-marker</p>")
    {
        var http = new DefaultHttpContext { User = PortalPrincipal.Create(email, null) };
        return Render<AppLayout>(ps => ps
            .AddCascadingValue<HttpContext>(http)
            .Add(l => l.Body, body));
    }

    [Fact]
    public void Renders_the_brand_and_the_vertical_nav()
    {
        var cut = RenderShell("dana@example.com");

        cut.Find(".navbar-vertical").ShouldNotBeNull();
        cut.Markup.ShouldContain("Crawl");
        cut.Markup.ShouldContain("dad");
        cut.FindAll("a.nav-link").Count.ShouldBe(5);
    }

    [Fact]
    public void Shows_the_signed_in_email_and_an_antiforgery_guarded_sign_out()
    {
        var cut = RenderShell("dana@example.com");

        cut.Find("[data-testid=user-email]").TextContent.ShouldContain("dana@example.com");

        var form = cut.Find("form[action=\"/auth/signout\"]");
        form.GetAttribute("method").ShouldBe("post");
        form.QuerySelector("input[name=__RequestVerificationToken]").ShouldNotBeNull();
        cut.Find("[data-testid=sign-out]").TextContent.ShouldContain("Sign out");
    }

    [Fact]
    public void Pins_a_queue_slot_usage_indicator_placeholder_to_the_sidebar()
    {
        var cut = RenderShell("dana@example.com");

        var usage = cut.Find("[data-testid=usage-indicator]");
        usage.QuerySelector("[data-testid=usage-slots]").ShouldNotBeNull();
        usage.QuerySelector("[data-testid=usage-queue]").ShouldNotBeNull();
        usage.QuerySelector(".progress-bar").ShouldNotBeNull();
    }

    [Fact]
    public void Renders_the_page_body()
    {
        var cut = RenderShell("dana@example.com", "<section>runs-here</section>");

        cut.Markup.ShouldContain("runs-here");
    }
}
