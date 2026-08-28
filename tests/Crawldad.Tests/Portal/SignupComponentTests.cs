using Bunit;
using Crawldad.Portal.Auth;
using Crawldad.Portal.Components.Pages;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>bUnit rendering of the signup component (issue #119 PR8): the "create your free workspace" framing over the SAME
/// two-step OTP wizard as the login page, the status/error notices, and client-side validation. The success path (cookie
/// issuance + the provision/landing branch) is covered by the HTTP flow tests + <see cref="SignupLandingTests"/>, not here —
/// exactly as the login component defers its success path to the login flow tests.</summary>
public class SignupComponentTests : BunitContext
{
    private readonly FakePortalAuthService _auth = new();

    public SignupComponentTests()
    {
        Services.AddSingleton<IPortalAuthService>(_auth);
        Services.AddSingleton<ISignupLanding>(new StubSignupLanding()); // resolved by @inject; never called on the non-success paths
        Services.AddSingleton<AntiforgeryStateProvider>(new StubAntiforgeryStateProvider());
    }

    private IRenderedComponent<Signup> RenderSignup() =>
        Render<Signup>(ps => ps.AddCascadingValue<HttpContext>(new DefaultHttpContext()));

    [Fact]
    public void Leads_with_the_create_free_workspace_framing_on_the_email_step()
    {
        var cut = RenderSignup();

        cut.Markup.ShouldContain("Create your free workspace");
        cut.Markup.ShouldContain("no credit card"); // the free-tier promise, not a sign-in framing
        cut.FindAll("#email").Count.ShouldBe(1);
        cut.FindAll("#code").ShouldBeEmpty();
        cut.Markup.ShouldContain("Email me a code");
        // A returning user still has a way to the sign-in page.
        cut.FindAll("a[href=\"/login\"]").Count.ShouldBe(1);
    }

    [Fact]
    public void Requesting_a_code_advances_to_the_verify_step()
    {
        _auth.RequestOutcome = RequestCodeOutcome.Sent;
        var cut = RenderSignup();

        cut.Find("#email").Change("founder@example.com");
        cut.Find("form").Submit();

        _auth.RequestedEmails.ShouldHaveSingleItem().ShouldBe("founder@example.com");
        cut.FindAll("#code").Count.ShouldBe(1);
        cut.Markup.ShouldContain("alert-info");
        cut.Markup.ShouldContain("Create workspace"); // the verify-step button carries the signup framing (HTML-escapes the &)
    }

    [Fact]
    public void Rate_limited_request_shows_the_polite_notice()
    {
        _auth.RequestOutcome = RequestCodeOutcome.RateLimited;
        var cut = RenderSignup();

        cut.Find("#email").Change("founder@example.com");
        cut.Find("form").Submit();

        cut.Markup.ShouldContain("few minutes");
    }

    [Fact]
    public void An_invalid_code_shows_an_error_and_stays_on_the_verify_step()
    {
        _auth.RequestOutcome = RequestCodeOutcome.Sent;
        _auth.VerifyResultToReturn = VerifyResult.Fail(VerifyOutcome.InvalidCode, "founder@example.com");
        var cut = RenderSignup();

        cut.Find("#email").Change("founder@example.com");
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
        var cut = RenderSignup();

        cut.Find("form").Submit();

        _auth.RequestedEmails.ShouldBeEmpty();
        cut.Markup.ShouldContain("Enter your email address");
    }

    // The success path never runs in these component renders (no auth service to issue a cookie), so this is a pure
    // placeholder that only needs to satisfy the page's @inject.
    private sealed class StubSignupLanding : ISignupLanding
    {
        public Task<string> ResolveAsync(string verifiedEmail, string? returnUrl, CancellationToken cancellationToken) =>
            Task.FromResult("/app");
    }
}
