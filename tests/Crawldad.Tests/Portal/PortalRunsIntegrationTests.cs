using System.Net;

namespace Crawldad.Tests.Portal;

/// <summary>HTTP-level behaviour of the runs surfaces booted in the real portal host: the run-detail route renders for an
/// authenticated (but, in the test host, unlinked) user, and the screenshot proxy is cookie-gated then a clean 404 for an
/// unlinked user — never a 500, never a leak.</summary>
[Collection(PortalCollection.Name)]
public class PortalRunsIntegrationTests(PortalFixture fixture)
{
    private static string NewEmail() => $"runs-{Guid.NewGuid():N}@example.com";

    private static string ScreenshotPath(Guid runId) => $"/app/runs/{runId}/screenshots/abc.png";

    [Fact]
    public async Task Run_detail_route_renders_the_not_linked_state_for_an_authenticated_user()
    {
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, NewEmail());

        var resp = await client.GetAsync(PortalHttp.Rel($"/app/runs/{Guid.NewGuid()}"));

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.ShouldContain("No workspace yet"); // the unlinked user's clean empty state, never a 500
    }

    [Fact]
    public async Task Replay_form_post_without_an_antiforgery_token_is_rejected()
    {
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, NewEmail());

        // The static-SSR replay form posts to the run-detail route; a post that omits the framework antiforgery token is
        // turned away by UseAntiforgery() before the component ever runs — the form is antiforgery-protected.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_handler"] = "replay-run",
            ["ReplayForm.Inputs"] = "{}",
            // deliberately no __RequestVerificationToken
        });
        var resp = await client.PostAsync(PortalHttp.Rel($"/app/runs/{Guid.NewGuid()}"), form);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Screenshot_proxy_redirects_an_unauthenticated_request_to_login()
    {
        using var client = fixture.NewClient();

        var resp = await client.GetAsync(PortalHttp.Rel(ScreenshotPath(Guid.NewGuid())));

        resp.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        resp.Headers.Location!.OriginalString.ShouldContain("/login");
    }

    [Fact]
    public async Task Screenshot_proxy_is_a_not_found_for_an_authenticated_but_unlinked_user()
    {
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, NewEmail());

        var resp = await client.GetAsync(PortalHttp.Rel(ScreenshotPath(Guid.NewGuid())));

        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
