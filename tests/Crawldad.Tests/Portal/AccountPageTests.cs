using System.Net;

namespace Crawldad.Tests.Portal;

/// <summary>Real-SSR behaviour of the account page over HTTP (Alba host, no live API): an authenticated zero-workspace
/// account renders the get-started state (the create-a-free-workspace affordance + the tucked-away claim form) without a
/// 500 and exposes the antiforgery-protected claim form; the post-claim redirect flag renders the success confirmation; and
/// a claim post without a valid antiforgery token is rejected before any handler runs.</summary>
[Collection(PortalCollection.Name)]
public class AccountPageTests(PortalFixture fixture)
{
    private static string NewEmail() => $"account-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task Zero_workspace_account_renders_the_get_started_state_and_the_claim_form_without_a_500()
    {
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, NewEmail());

        var resp = await client.GetAsync(PortalHttp.Rel("/app/account"));

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.ShouldContain("No workspace yet");                   // profile status (console-mode, no active workspace)
        html.ShouldContain("data-testid=\"provision-form\"");      // the ONE create-a-free-workspace affordance
        html.ShouldContain("Create your free workspace");
        html.ShouldContain("id=\"link-form\"");                    // the tucked-away claim form is present
        html.ShouldContain("__RequestVerificationToken");          // ...antiforgery-protected
        html.ShouldNotContain("Workspace ready.");                 // no success banner on a plain GET
    }

    [Fact]
    public async Task The_post_claim_redirect_flag_renders_the_success_confirmation()
    {
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, NewEmail());

        var resp = await client.GetAsync(PortalHttp.Rel("/app/account?linked=true"));

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.ShouldContain("Workspace ready.");
    }

    [Fact]
    public async Task A_link_post_without_a_valid_antiforgery_token_is_rejected()
    {
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, NewEmail());

        // A well-formed workspace-link post but with a bogus token: antiforgery rejects it before the page handler (and
        // therefore before any API call) ever runs — so a wrong key can never even be validated without a real token.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_handler"] = "workspace-link",
            ["__RequestVerificationToken"] = "not-a-real-token",
            ["Input.TenantId"] = "tenant-alpha",
            ["Input.ApiKey"] = "should-never-be-validated",
        });

        var resp = await client.PostAsync(PortalHttp.Rel("/app/account"), form);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
