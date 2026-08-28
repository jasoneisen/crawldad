using System.Net;

namespace Crawldad.Tests.Portal;

/// <summary>End-to-end public-signup flow over real HTTP (issue #119 PR8): the SAME antiforgery-guarded, enumeration-safe
/// two-step OTP the login flow uses, posted to <c>/signup</c>, plus the post-verification landing. The shared test host is
/// unconfigured for console auth, so it exercises the honest STORED-KEY behaviour: signup signs the account in but cannot
/// provision, so it lands on the account page's attach-a-workspace state (never a 500, never a hidden dead button). The
/// console-mode provisioning landing is covered by <see cref="SignupLandingTests"/>; login stays byte-identical (its suites
/// are untouched).</summary>
[Collection(PortalCollection.Name)]
public class SignupFlowTests(PortalFixture fixture)
{
    private static string NewEmail() => $"signup-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task Signup_signs_in_and_lands_on_the_account_attach_state_in_stored_key_mode()
    {
        var email = NewEmail();
        using var client = fixture.NewClient();

        var token = PortalHttp.ExtractAntiforgeryToken(await client.GetStringAsync(PortalHttp.Rel("/signup")));
        var requestResp = await client.PostAsync(PortalHttp.Rel("/signup"), PortalHttp.SignupForm(token, email, "request"));
        requestResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var code = fixture.App.Email.LastCodeFor(email);
        var verifyToken = PortalHttp.ExtractAntiforgeryToken(await requestResp.Content.ReadAsStringAsync());
        var verifyResp = await client.PostAsync(PortalHttp.Rel("/signup"), PortalHttp.SignupForm(verifyToken, email, "verify", code));

        // Verified → signed in → stored-key mode can't provision → the honest account-page landing.
        verifyResp.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        verifyResp.Headers.Location!.OriginalString.ShouldEndWith("/app/account");

        var accountResp = await client.GetAsync(PortalHttp.Rel("/app/account"));
        accountResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await accountResp.Content.ReadAsStringAsync();
        html.ShouldContain(email);                                    // the account is genuinely signed in
        html.ShouldContain("Attach a workspace below to get started"); // the operator-provisioned reality (the attach form path)
        html.ShouldNotContain("data-testid=\"provision-form\"");        // no self-serve provision affordance in stored-key mode
    }

    [Fact]
    public async Task The_request_step_uses_the_same_neutral_enumeration_safe_notice_as_login()
    {
        var email = NewEmail();
        using var client = fixture.NewClient();

        var token = PortalHttp.ExtractAntiforgeryToken(await client.GetStringAsync(PortalHttp.Rel("/signup")));
        var resp = await client.PostAsync(PortalHttp.Rel("/signup"), PortalHttp.SignupForm(token, email, "request"));

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.ShouldContain("id=\"code\"");                 // advanced to the verify step
        html.ShouldContain("sign-in code is on its way");  // the byte-identical neutral copy — no account-existence oracle
        html.ShouldNotContain("alert-danger");
    }

    [Fact]
    public async Task An_invalid_code_shows_an_error_and_stays_on_the_verify_step()
    {
        var email = NewEmail();
        using var client = fixture.NewClient();

        var token = PortalHttp.ExtractAntiforgeryToken(await client.GetStringAsync(PortalHttp.Rel("/signup")));
        var requestResp = await client.PostAsync(PortalHttp.Rel("/signup"), PortalHttp.SignupForm(token, email, "request"));
        var verifyToken = PortalHttp.ExtractAntiforgeryToken(await requestResp.Content.ReadAsStringAsync());

        var verifyResp = await client.PostAsync(PortalHttp.Rel("/signup"), PortalHttp.SignupForm(verifyToken, email, "verify", "WRONG9"));

        verifyResp.StatusCode.ShouldBe(HttpStatusCode.OK); // no redirect — the failed verify re-renders in place
        var html = await verifyResp.Content.ReadAsStringAsync();
        html.ShouldContain("alert-danger");
        html.ShouldContain("id=\"code\"");
    }

    [Fact]
    public async Task The_return_url_is_seeded_from_the_query_and_survives_the_two_step_flow()
    {
        var email = NewEmail();
        using var client = fixture.NewClient();

        // GET seeds the hidden field from the query (the initial-GET branch of OnParametersSet).
        var seededHtml = await client.GetStringAsync(PortalHttp.Rel("/signup?ReturnUrl=%2Fapp%2Fpayloads"));
        seededHtml.ShouldContain("/app/payloads");
        var token = PortalHttp.ExtractAntiforgeryToken(seededHtml);

        // POST both steps carrying the return url: once the form carries it, the posted value wins (the "already set" branch).
        var requestResp = await client.PostAsync(PortalHttp.Rel("/signup"),
            PortalHttp.SignupForm(token, email, "request", returnUrl: "/app/payloads"));
        var verifyHtml = await requestResp.Content.ReadAsStringAsync();
        verifyHtml.ShouldContain("/app/payloads"); // still carried on the re-rendered verify step
        var code = fixture.App.Email.LastCodeFor(email);
        var verifyToken = PortalHttp.ExtractAntiforgeryToken(verifyHtml);
        var verifyResp = await client.PostAsync(PortalHttp.Rel("/signup"),
            PortalHttp.SignupForm(verifyToken, email, "verify", code, returnUrl: "/app/payloads"));

        // Stored-key mode still can't provision, so a zero-workspace account lands on the account page regardless of the
        // return url (the return url is honoured only for a returning, already-linked account — see SignupLandingTests).
        verifyResp.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        verifyResp.Headers.Location!.OriginalString.ShouldEndWith("/app/account");
    }

    [Fact]
    public async Task A_signup_post_without_a_valid_antiforgery_token_is_rejected()
    {
        using var client = fixture.NewClient();

        var form = PortalHttp.SignupForm("not-a-real-token", NewEmail(), "request");
        var resp = await client.PostAsync(PortalHttp.Rel("/signup"), form);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest); // antiforgery rejects it before any handler runs
    }
}
