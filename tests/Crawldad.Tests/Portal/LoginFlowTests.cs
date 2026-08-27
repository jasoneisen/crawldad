using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Crawldad.Tests.Portal;

/// <summary>End-to-end OTP flow over real HTTP (WebApplicationFactory): antiforgery-guarded form posts, the auth
/// cookie, and the authenticated shell.</summary>
[Collection(PortalCollection.Name)]
public class LoginFlowTests(PortalFixture fixture)
{
    private static string NewEmail() => $"{Guid.NewGuid():N}@example.com";

    [Fact]
    public void Shared_host_runs_in_development()
    {
        var env = fixture.App.Services.GetRequiredService<IHostEnvironment>();
        env.IsDevelopment().ShouldBeTrue();
    }

    [Fact]
    public async Task Full_flow_requests_verifies_and_signs_in()
    {
        var email = NewEmail();
        using var client = fixture.NewClient();

        var token = PortalHttp.ExtractAntiforgeryToken(await client.GetStringAsync(PortalHttp.Rel("/login")));
        var requestResp = await client.PostAsync(PortalHttp.Rel("/login"), PortalHttp.LoginForm(token, email, "request"));
        requestResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var code = fixture.App.Email.LastCodeFor(email);
        var verifyToken = PortalHttp.ExtractAntiforgeryToken(await requestResp.Content.ReadAsStringAsync());
        var verifyResp = await client.PostAsync(PortalHttp.Rel("/login"), PortalHttp.LoginForm(verifyToken, email, "verify", code));

        verifyResp.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        verifyResp.Headers.Location!.OriginalString.ShouldEndWith("/app");

        var appResp = await client.GetAsync(PortalHttp.Rel("/app"));
        appResp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var appHtml = await appResp.Content.ReadAsStringAsync();
        appHtml.ShouldContain(email);
        appHtml.ShouldContain("Sign out");
    }
}
