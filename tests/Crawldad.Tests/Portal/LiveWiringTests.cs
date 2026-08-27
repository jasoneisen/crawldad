using Crawldad.Portal.Tenancy;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>Host-level wiring for the interactive live-trace page: the circuit-safe tenant resolver resolves as a scoped
/// service, and the framework's <see cref="AuthenticationStateProvider"/> (which the resolver reads the signed-in user
/// from on a circuit) is registered by the Blazor Web App + cascading-auth-state wiring.</summary>
[Collection(PortalCollection.Name)]
public class LiveWiringTests(PortalFixture fixture)
{
    [Fact]
    public void The_circuit_tenant_resolver_resolves_as_a_scoped_service()
    {
        using var scope = fixture.App.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICircuitTenantResolver>().ShouldNotBeNull();
    }

    [Fact]
    public void An_authentication_state_provider_is_registered_for_the_circuit_path()
    {
        using var scope = fixture.App.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>().ShouldNotBeNull();
    }
}
