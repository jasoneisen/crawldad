using System.Net;

namespace Crawldad.Tests.Portal;

/// <summary>HTTP-level behaviour of the portal shell: authorization, sign-out, enumeration safety, return-url
/// handling, and the static pages.</summary>
[Collection(PortalCollection.Name)]
public class PortalIntegrationTests(PortalFixture fixture)
{
    private static string NewEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task Unauthenticated_app_request_redirects_to_login_with_return_url()
    {
        using var client = fixture.NewClient();

        var resp = await client.GetAsync(PortalHttp.Rel("/app/runs"));

        resp.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        resp.Headers.Location!.OriginalString.ShouldContain("/login");
        resp.Headers.Location!.OriginalString.ShouldContain("ReturnUrl");
    }

    [Fact]
    public async Task Sign_out_clears_the_cookie()
    {
        var email = NewEmail("signout");
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, email);

        (await client.GetAsync(PortalHttp.Rel("/app"))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var token = PortalHttp.ExtractAntiforgeryToken(await client.GetStringAsync(PortalHttp.Rel("/app")));
        var signout = await client.PostAsync(PortalHttp.Rel("/auth/signout"), PortalHttp.TokenForm(token));

        signout.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        signout.Headers.Location!.OriginalString.ShouldEndWith("/");
        // The cookie is gone → /app is no longer authorized.
        (await client.GetAsync(PortalHttp.Rel("/app"))).StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Sign_out_honors_a_local_return_url()
    {
        using var client = fixture.NewClient();
        var token = PortalHttp.ExtractAntiforgeryToken(await client.GetStringAsync(PortalHttp.Rel("/login")));

        var resp = await client.PostAsync(PortalHttp.Rel("/auth/signout"), PortalHttp.TokenForm(token, "/login"));

        resp.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        resp.Headers.Location!.OriginalString.ShouldEndWith("/login");
    }

    [Fact]
    public async Task Requesting_a_code_behaves_identically_for_known_and_unknown_addresses()
    {
        // Make one address "known" by completing a sign-in first.
        var known = NewEmail("known");
        using (var signInClient = fixture.NewClient())
        {
            await PortalHttp.SignInAsync(signInClient, fixture.App.Email, known);
        }
        var unknown = NewEmail("unknown");

        var knownResponse = await RequestStepAsync(known);
        var unknownResponse = await RequestStepAsync(unknown);

        // Same status, both advance to the verify step, both show the neutral "sent" notice, neither errors.
        foreach (var html in new[] { knownResponse, unknownResponse })
        {
            html.ShouldContain("id=\"code\"");
            html.ShouldContain("sign-in code is on its way");
            html.ShouldNotContain("alert-danger");
        }
    }

    [Fact]
    public async Task Return_url_survives_the_two_step_flow()
    {
        var email = NewEmail("returnurl");
        using var client = fixture.NewClient();

        var loginHtml = await client.GetStringAsync(PortalHttp.Rel("/login?ReturnUrl=%2Fapp%2Fruns"));
        loginHtml.ShouldContain("/app/runs"); // seeded into the hidden field
        var token = PortalHttp.ExtractAntiforgeryToken(loginHtml);

        var requestResp = await client.PostAsync(PortalHttp.Rel("/login"),
            PortalHttp.LoginForm(token, email, "request", returnUrl: "/app/runs"));
        var code = fixture.App.Email.LastCodeFor(email);
        var verifyToken = PortalHttp.ExtractAntiforgeryToken(await requestResp.Content.ReadAsStringAsync());
        var verifyResp = await client.PostAsync(PortalHttp.Rel("/login"),
            PortalHttp.LoginForm(verifyToken, email, "verify", code, returnUrl: "/app/runs"));

        verifyResp.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        verifyResp.Headers.Location!.OriginalString.ShouldEndWith("/app/runs");
    }

    [Fact]
    public async Task An_invalid_code_shows_an_error_and_stays_on_the_verify_step()
    {
        var email = NewEmail("bad");
        using var client = fixture.NewClient();

        var token = PortalHttp.ExtractAntiforgeryToken(await client.GetStringAsync(PortalHttp.Rel("/login")));
        var requestResp = await client.PostAsync(PortalHttp.Rel("/login"), PortalHttp.LoginForm(token, email, "request"));
        var verifyToken = PortalHttp.ExtractAntiforgeryToken(await requestResp.Content.ReadAsStringAsync());

        var verifyResp = await client.PostAsync(PortalHttp.Rel("/login"),
            PortalHttp.LoginForm(verifyToken, email, "verify", "WRONG9"));

        verifyResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await verifyResp.Content.ReadAsStringAsync();
        html.ShouldContain("alert-danger");
        html.ShouldContain("id=\"code\"");
    }

    [Fact]
    public async Task Marketing_home_renders_the_hero()
    {
        using var client = fixture.NewClient();

        var html = await client.GetStringAsync(PortalHttp.Rel("/"));

        html.ShouldContain("Crawldad");
        html.ShouldContain("/docs");
        html.ShouldContain("/login");
    }

    [Fact]
    public async Task Error_page_is_reachable()
    {
        using var client = fixture.NewClient();

        var resp = await client.GetAsync(PortalHttp.Rel("/error"));

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).ShouldContain("Something went wrong");
    }

    [Fact]
    public async Task Unknown_route_renders_not_found()
    {
        using var client = fixture.NewClient();

        var resp = await client.GetAsync(PortalHttp.Rel("/no-such-page"));

        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await resp.Content.ReadAsStringAsync()).ShouldContain("Page not found");
    }

    private async Task<string> RequestStepAsync(string email)
    {
        using var client = fixture.NewClient();
        var token = PortalHttp.ExtractAntiforgeryToken(await client.GetStringAsync(PortalHttp.Rel("/login")));
        var resp = await client.PostAsync(PortalHttp.Rel("/login"), PortalHttp.LoginForm(token, email, "request"));
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await resp.Content.ReadAsStringAsync();
    }
}
