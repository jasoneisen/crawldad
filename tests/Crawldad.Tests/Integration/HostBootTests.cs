using Alba;
using Crawldad.Contracts;

namespace Crawldad.Tests.Integration;

/// <summary>Boots the whole host through Alba and drives <c>/health</c> over real HTTP. A 200 with the expected JSON
/// body proves <c>HostConfiguration</c> composed — Marten, Wolverine, the Wolverine.Http pipeline, and the shared
/// JSON wire convention — and it is what covers that configuration.</summary>
[Collection(IntegrationCollection.Name)]
public class HostBootTests(AppFixture fixture)
{
    private IAlbaHost Host => fixture.Host;

    [Fact]
    public async Task Health_endpoint_reports_ok()
    {
        var result = await Host.Scenario(x =>
        {
            x.Get.Url("/health");
            x.StatusCodeShouldBeOk();
        });

        var body = await result.ReadAsJsonAsync<HealthStatus>();
        body!.Status.ShouldBe("ok");
    }
}
