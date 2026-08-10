using Alba;
using Crawldad.Tests.Support;
using Microsoft.AspNetCore.Hosting;

namespace Crawldad.Tests.Integration;

/// <summary>Boots the host in Production to cover the non-development pipeline branch (ProblemDetails handler,
/// HSTS, skipped dev-only schema apply). Uses its own schema but shares the integration collection so it never
/// races the dev fixture on the same Postgres.</summary>
[Collection(IntegrationCollection.Name)]
public class ProductionHostTests
{
    [Fact]
    public async Task Host_serves_health_in_production()
    {
        await using var host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseCrawldadTestDefaults("crawldad_prodtest");
        });

        await host.Scenario(x =>
        {
            x.Get.Url("/health");
            x.StatusCodeShouldBeOk();
        });
    }
}
