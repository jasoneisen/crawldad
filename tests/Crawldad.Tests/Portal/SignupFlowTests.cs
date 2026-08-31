using System.Net;

namespace Crawldad.Tests.Portal;

/// <summary>End-to-end public-signup flow over real HTTP (issue #119): the SAME antiforgery-guarded, enumeration-safe
/// two-step OTP the login flow uses, posted to <c>/signup</c>, plus the post-verification landing. The shared test host runs
/// in console-mode (fake token source) but has no live API, so a zero-workspace signup provisions, the provision fails
/// against the unreachable API, and it lands on the account page's get-started state carrying a safe error (never a 500,
/// never a hidden dead button). The happy-path provisioning landing is covered by <see cref="SignupLandingTests"/> over a
/// stub API; login stays byte-identical (its suites are untouched).</summary>
[Collection(PortalCollection.Name)]
public class SignupFlowTests(PortalFixture fixture)
{
    private static string NewEmail() => $"signup-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task Signup_signs_in_provisions_and_a_failure_lands_on_the_account_get_started_state()
    {
        var email = NewEmail();
        using var client = fixture.NewClient();

        var token = PortalHttp.ExtractAntiforgeryToken(await client.GetStringAsync(PortalHttp.Rel("/signup")));
        var requestResp = await client.PostAsync(PortalHttp.Rel("/signup"), PortalHttp.SignupForm(token, email, "request"));
        requestResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var code = fixture.App.Email.LastCodeFor(email);
        var verifyToken = PortalHttp.ExtractAntiforgeryToken(await requestResp.Content.ReadAsStringAsync());
        var verifyResp = await client.PostAsync(PortalHttp.Rel("/signup"), PortalHttp.SignupForm(verifyToken, email, "verify", code));

        // Verified → signed in → console-mode provisions, but the test host has no live API, so the provision fails and the
        // signup lands on the account page's get-started state carrying a safe error (never a 500).
        verifyResp.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = verifyResp.Headers.Location!;
        location.OriginalString.ShouldContain("/app/account");
        location.OriginalString.ShouldContain("provisionError");

        var accountResp = await client.GetAsync(location);
        accountResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await accountResp.Content.ReadAsStringAsync();
        html.ShouldContain(email);                                // the account is genuinely signed in
        html.ShouldContain("data-testid=\"provision-form\"");      // console-mode shows the self-serve create affordance
        html.ShouldContain("data-testid=\"provision-error\"");     // the safe provision-failure message is surfaced
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

        // A zero-workspace account is provisioned rather than honouring the return url; the test host has no live API, so the
        // provision fails and it lands on the account page — never /app/payloads. The return url is honoured only for a
        // returning, already-active account (see SignupLandingTests).
        verifyResp.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = verifyResp.Headers.Location!.OriginalString;
        location.ShouldContain("/app/account");
        location.ShouldNotContain("/app/payloads");
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
