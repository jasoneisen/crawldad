using System.Net;

namespace Crawldad.Tests.Portal;

/// <summary>Real-SSR behaviour of the account page over HTTP (Alba host, no live API): an authenticated but unlinked
/// account renders every section without a 500 and exposes the antiforgery-protected workspace-link form; the
/// post-link redirect flag renders the success confirmation; and a form post without a valid antiforgery token is
/// rejected before any handler runs.</summary>
[Collection(PortalCollection.Name)]
public class AccountPageTests(PortalFixture fixture)
{
    private static string NewEmail() => $"account-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task Unlinked_account_renders_every_section_and_the_link_form_without_a_500()
    {
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, NewEmail());

        var resp = await client.GetAsync(PortalHttp.Rel("/app/account"));

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.ShouldContain("Not linked");         // profile status
        html.ShouldContain("No usage yet");        // usage empty state
        html.ShouldContain("Billing managed via Stripe"); // billing placeholder
        html.ShouldContain("API keys");            // operator-managed info card
        html.ShouldContain("id=\"link-form\"");    // the workspace-link form is present
        html.ShouldContain("__RequestVerificationToken"); // ...antiforgery-protected
        html.ShouldNotContain("Workspace linked."); // no success banner on a plain GET
    }

    [Fact]
    public async Task The_post_link_redirect_flag_renders_the_success_confirmation()
    {
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, NewEmail());

        var resp = await client.GetAsync(PortalHttp.Rel("/app/account?linked=true"));

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.ShouldContain("Workspace linked.");
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
