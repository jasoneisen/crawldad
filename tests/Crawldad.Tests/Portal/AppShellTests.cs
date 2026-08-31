using System.Net;

namespace Crawldad.Tests.Portal;

/// <summary>HTTP-level behaviour of the authenticated app section: every /app route is cookie-gated (an
/// unauthenticated hit 302s to /login with a return url), and once signed in each route renders its page inside
/// the shell. The folded-away usage route no longer resolves.</summary>
[Collection(PortalCollection.Name)]
public class AppShellTests(PortalFixture fixture)
{
    private static string NewEmail() => $"shell-{Guid.NewGuid():N}@example.com";

    // Route → a plain-text marker unique to that page's empty state / overview. The test user is authenticated but has
    // no tenant link (no Portal:DevTenantLink in the test host), so the data-backed runs page shows its not-linked state.
    private static readonly (string Route, string Marker)[] _sections =
    [
        ("/app", "Welcome to Crawldad"),
        ("/app/runs", "No workspace yet"),
        ("/app/live", "No workspace yet"), // the live picker now resolves the tenant; unlinked → not-linked state
        ("/app/payloads", "No workspace yet"),
        ("/app/webhooks", "No workspace yet"),
        ("/app/account", "Create your free workspace"), // zero-workspace account → the get-started affordance
    ];

    public static TheoryData<string> Routes()
    {
        var data = new TheoryData<string>();
        foreach (var (route, _) in _sections)
        {
            data.Add(route);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Unauthenticated_app_route_redirects_to_login_with_return_url(string route)
    {
        using var client = fixture.NewClient();

        var resp = await client.GetAsync(PortalHttp.Rel(route));

        resp.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!.OriginalString;
        location.ShouldContain("/login");
        location.ShouldContain("ReturnUrl");
        location.ShouldContain(Uri.EscapeDataString(route));
    }

    [Fact]
    public async Task Authenticated_user_sees_every_section_rendered_in_the_shell()
    {
        var email = NewEmail();
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, email);

        foreach (var (route, marker) in _sections)
        {
            var resp = await client.GetAsync(PortalHttp.Rel(route));

            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            var html = await resp.Content.ReadAsStringAsync();
            // The page rendered its own content...
            html.ShouldContain(marker);
            // ...wrapped in the authenticated shell (email + sign-out + the five nav links).
            html.ShouldContain(email);
            html.ShouldContain("Sign out");
            html.ShouldContain("/app/webhooks");
        }
    }

    [Fact]
    public async Task The_folded_usage_route_no_longer_resolves()
    {
        var email = NewEmail();
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, email);

        var resp = await client.GetAsync(PortalHttp.Rel("/app/usage"));

        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
