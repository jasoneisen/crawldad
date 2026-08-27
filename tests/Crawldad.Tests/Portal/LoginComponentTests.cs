using Bunit;
using Crawldad.Portal.Auth;
using Crawldad.Portal.Components.Pages;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the login component: the two-step wizard, the status/error notices, and
/// client-side validation. The success path (cookie issuance) is covered by the HTTP flow tests, not here.</summary>
public class LoginComponentTests : BunitContext
{
    private readonly FakePortalAuthService _auth = new();

    public LoginComponentTests()
    {
        Services.AddSingleton<IPortalAuthService>(_auth);
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());
    }

    private IRenderedComponent<Login> RenderLogin() =>
        Render<Login>(ps => ps.AddCascadingValue<HttpContext>(new DefaultHttpContext()));

    [Fact]
    public void Starts_on_the_email_step()
    {
        var cut = RenderLogin();

        cut.FindAll("#email").Count.ShouldBe(1);
        cut.FindAll("#code").ShouldBeEmpty();
        cut.Markup.ShouldContain("Email me a code");
    }

    [Fact]
    public void Requesting_a_code_advances_to_the_verify_step()
    {
        _auth.RequestOutcome = RequestCodeOutcome.Sent;
        var cut = RenderLogin();

        cut.Find("#email").Change("user@example.com");
        cut.Find("form").Submit();

        _auth.RequestedEmails.ShouldHaveSingleItem().ShouldBe("user@example.com");
        cut.FindAll("#code").Count.ShouldBe(1);
        cut.Markup.ShouldContain("alert-info");
    }

    [Fact]
    public void Rate_limited_request_shows_the_polite_notice()
    {
        _auth.RequestOutcome = RequestCodeOutcome.RateLimited;
        var cut = RenderLogin();

        cut.Find("#email").Change("user@example.com");
        cut.Find("form").Submit();

        cut.Markup.ShouldContain("few minutes");
    }

    [Fact]
    public void An_invalid_code_shows_an_error_and_stays_on_the_verify_step()
    {
        _auth.RequestOutcome = RequestCodeOutcome.Sent;
        _auth.VerifyResultToReturn = VerifyResult.Fail(VerifyOutcome.InvalidCode, "user@example.com");
        var cut = RenderLogin();

        cut.Find("#email").Change("user@example.com");
        cut.Find("form").Submit();
        cut.Find("#code").Change("WRONG9");
        cut.Find("form").Submit();

        cut.Markup.ShouldContain("alert-danger");
        cut.Markup.ShouldContain("isn't right");
        cut.FindAll("#code").Count.ShouldBe(1);
    }

    [Fact]
    public void A_blank_email_is_rejected_without_calling_the_service()
    {
        var cut = RenderLogin();

        cut.Find("form").Submit();

        _auth.RequestedEmails.ShouldBeEmpty();
        cut.Markup.ShouldContain("Enter your email address");
    }
}
