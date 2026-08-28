using System.Collections.Generic;
using Crawldad.Portal.Infrastructure.Security;
using Crawldad.Portal.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>The portal's console-mode wiring (issue #119 PR4): with no <c>Crawldad:ConsoleAuth</c> config nothing but the
/// options + guard is registered (the byte-identical stored-key path stands); with both knobs set the managed-identity
/// token source and the console client factory are registered. The token-source construction is I/O-free, so the Azure
/// branch is covered hermetically (no live identity), exactly like the Data-Protection module.</summary>
public class PortalConsoleAuthModuleTests
{
    private static ServiceCollection Wire(string? tenantId, string? audience)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (tenantId is not null)
        {
            settings["Crawldad:ConsoleAuth:TenantId"] = tenantId;
        }

        if (audience is not null)
        {
            settings["Crawldad:ConsoleAuth:Audience"] = audience;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        PortalConsoleAuthModule.AddConsoleAuth(services, config);
        return services;
    }

    [Fact]
    public void No_config_registers_no_token_source_or_factory()
    {
        var services = Wire(tenantId: null, audience: null);

        services.Any(descriptor => descriptor.ServiceType == typeof(IConsoleTokenSource)).ShouldBeFalse();
        services.Any(descriptor => descriptor.ServiceType == typeof(ConsoleClientFactory)).ShouldBeFalse();
    }

    [Fact]
    public void A_half_configured_pair_registers_no_token_source()
    {
        // Only the audience is set — the registration gate is both-present, so nothing console is wired (the boot validator
        // turns this into a loud startup failure, covered in its own tests).
        var services = Wire(tenantId: null, audience: "api://crawldad-api-stg");

        services.Any(descriptor => descriptor.ServiceType == typeof(IConsoleTokenSource)).ShouldBeFalse();
    }

    [Fact]
    public void Both_knobs_set_register_the_managed_identity_token_source_and_factory()
    {
        var services = Wire("11111111-2222-3333-4444-555555555555", "api://crawldad-api-stg");

        services.Any(descriptor => descriptor.ServiceType == typeof(ConsoleClientFactory)).ShouldBeTrue();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IConsoleTokenSource>().ShouldBeOfType<ManagedIdentityConsoleTokenSource>();
    }

    [Fact]
    public void AddConsoleAuth_rejects_null_arguments()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        Should.Throw<ArgumentNullException>(() => PortalConsoleAuthModule.AddConsoleAuth(null!, config));
        Should.Throw<ArgumentNullException>(() => PortalConsoleAuthModule.AddConsoleAuth(services, null!));
    }
}
