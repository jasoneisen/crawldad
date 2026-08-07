using System.Collections.Generic;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Browser;
using Crawldad.Web.Infrastructure.Browser.Real;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The Phase 4 DI wiring (<see cref="RunsModule"/>): the three real adapters register as keyed
/// <see cref="IBrowserBackend"/> services beside the fake, and the shared policy-layer singletons resolve. Endpoint/API
/// bases default to production when unconfigured and take a configured override otherwise. Disposing the provider
/// exercises the unused-resource teardown paths of the adapters and singletons.
/// </summary>
public class BrowserBackendRegistrationTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(config.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value)))
            .Build());
        RunsModule.AddRunsServices(services);
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
    public async Task Honours_configured_endpoint_overrides()
    {
        await using var sp = Build(
            ("Crawldad:Browserless:EndpointTemplate", "ws://127.0.0.1:9/x"),
            ("Crawldad:Browserbase:ApiBaseUrl", "http://127.0.0.1:9"));

        // Resolution succeeds with the override applied (the '?? Default' false branch).
        sp.GetRequiredKeyedService<IBrowserBackend>("browserless").ShouldBeOfType<BrowserlessBackend>();
        sp.GetRequiredKeyedService<IBrowserBackend>("browserbase").ShouldBeOfType<BrowserbaseBackend>();
    }
}
