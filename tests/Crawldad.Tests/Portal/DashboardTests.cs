using System.Net;

namespace Crawldad.Tests.Portal;

/// <summary>The authenticated dashboard's first-run on-ramp (issue #119 PR8): a signup lands here as <c>/app?welcome=1</c>,
/// which renders a brief empty-state pointing at the API keys + payloads on-ramp; every other visit is unchanged. Driven over
/// real HTTP so the <c>?welcome=</c> query binding and the <c>[Authorize]</c> gating are exercised for real.</summary>
[Collection(PortalCollection.Name)]
public class DashboardTests(PortalFixture fixture)
{
    private static string NewEmail() => $"dash-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task The_first_run_welcome_renders_when_signposted()
    {
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, NewEmail());

        var html = await client.GetStringAsync(PortalHttp.Rel("/app?welcome=true"));

        html.ShouldContain("data-testid=\"first-run-welcome\"");
        html.ShouldContain("Your free workspace is ready");
        html.ShouldContain("no new infra, no VPN"); // the MARKETING.md on-ramp phrasing, verbatim
        html.ShouldContain("/app/account#apikeys"); // mint an API key
        html.ShouldContain("/app/payloads");        // declare a payload
    }

    [Fact]
    public async Task The_dashboard_omits_the_welcome_by_default()
    {
        using var client = fixture.NewClient();
        await PortalHttp.SignInAsync(client, fixture.App.Email, NewEmail());

        var html = await client.GetStringAsync(PortalHttp.Rel("/app"));

        html.ShouldNotContain("data-testid=\"first-run-welcome\"");
        html.ShouldNotContain("Your free workspace is ready");
        html.ShouldContain("Runs"); // the ordinary dashboard section cards still render
    }
}
