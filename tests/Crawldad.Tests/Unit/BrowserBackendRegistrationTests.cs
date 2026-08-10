using System.Collections.Generic;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Real;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Unit;

/// <summary>The DI wiring (<see cref="RunsModule"/>): the three real adapters register as keyed
/// <see cref="IBrowserBackend"/> services beside the fake, and the shared policy-layer singletons resolve;
/// endpoint/API bases default to production unless overridden. Disposing exercises adapter/singleton teardown.</summary>
public class BrowserBackendRegistrationTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(config.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value)))
            .Build());
        RunsModule.AddRunsServices(services);
        // The credentialed adapters resolve through IConnectCredentialResolver (wired by BrowsersModule in the app); a
        // fake stands in so this RunsModule-only container can build them without the Marten-backed store.
        services.AddSingleton<IConnectCredentialResolver>(new FixedConnectResolver("x"));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Registers_the_real_adapters_and_shared_singletons_with_defaults()
    {
        await using var sp = Build();

        sp.GetRequiredKeyedService<IBrowserBackend>("local").ShouldBeOfType<LocalChromiumBackend>();
        sp.GetRequiredKeyedService<IBrowserBackend>("browserless").ShouldBeOfType<BrowserlessBackend>();
        sp.GetRequiredKeyedService<IBrowserBackend>("browserbase").ShouldBeOfType<BrowserbaseBackend>();
        sp.GetRequiredKeyedService<IBrowserBackend>("fake").ShouldBeOfType<Crawldad.Web.Infrastructure.Browser.Fake.FakeBrowserBackend>();

        sp.GetRequiredService<ISecretStore>().ShouldBeOfType<ConfigurationSecretStore>();
        sp.GetRequiredService<IAssetCache>().ShouldBeOfType<InMemoryAssetCache>();
        sp.GetRequiredService<IThrottleGate>().ShouldBeOfType<ThrottleGate>();
        sp.GetRequiredService<IPlaywrightProvider>().ShouldBeOfType<PlaywrightProvider>();

        // The backend registry resolves the real adapters by data.
        var registry = sp.GetRequiredService<IBrowserBackendRegistry>();
        registry.TryResolve("browserless", out var browserless).ShouldBeTrue();
        browserless.ShouldNotBeNull();
    }

    [Fact]
    public async Task Registers_the_config_secret_vault_behind_the_keyed_registry()
    {
        await using var sp = Build();

        // The secret-vault registry resolves the `config` adapter by kind (the BYO-vault seam); an unregistered kind
        // (a not-yet-built azure-keyvault/etc.) is a clean miss — the interpreter turns that into unknown_secret_vault.
        var registry = sp.GetRequiredService<ISecretStoreRegistry>();
        registry.ShouldBeOfType<KeyedSecretStoreRegistry>();
        registry.TryResolve(SecretVaults.Config, out var vault).ShouldBeTrue();
        vault.ShouldBeOfType<ConfigurationSecretStore>();
        registry.TryResolve("azure-keyvault", out _).ShouldBeFalse();

        // The plain ISecretStore (backend connect) and the keyed `config` vault (form-fill) are the one shared instance.
        sp.GetRequiredKeyedService<ISecretStore>(SecretVaults.Config).ShouldBeSameAs(sp.GetRequiredService<ISecretStore>());
    }

    [Fact]
    public async Task Honours_configured_endpoint_overrides()
    {
        await using var sp = Build(
            ("Crawldad:Browserless:EndpointTemplate", "ws://127.0.0.1:9/x"),
            ("Crawldad:Browserbase:ApiBaseUrl", "http://127.0.0.1:9"));

        sp.GetRequiredKeyedService<IBrowserBackend>("browserless").ShouldBeOfType<BrowserlessBackend>();
        sp.GetRequiredKeyedService<IBrowserBackend>("browserbase").ShouldBeOfType<BrowserbaseBackend>();
    }
}
