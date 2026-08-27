using Crawldad.Portal.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>Host-level wiring: the SDK's pooled HttpClient carries the configured API base address, and the
/// per-request tenant context resolves from the DI graph as a scoped service.</summary>
[Collection(PortalCollection.Name)]
public class PortalTenantWiringTests(PortalFixture fixture)
{
    [Fact]
    public void The_named_api_client_is_registered_with_the_configured_base_address()
    {
        var client = fixture.App.Services.GetRequiredService<IHttpClientFactory>().CreateClient(PortalTenancy.ApiHttpClientName);

        client.BaseAddress.ShouldBe(new Uri("http://localhost:5291/")); // from appsettings Crawldad:Api:BaseUrl
    }

    [Fact]
    public void The_tenant_context_resolves_as_a_scoped_service()
    {
        using var scope = fixture.App.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPortalTenantContext>().ShouldNotBeNull();
    }
}
