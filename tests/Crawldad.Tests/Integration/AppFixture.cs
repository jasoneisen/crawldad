using Alba;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>One Alba host shared by every integration test class via the collection fixture below, so the host
/// (and its schema migration) is built once. Demonstrates the DI-override seam: a pinned clock replaces the real
/// <see cref="TimeProvider"/>; other test hosts override the browser seam the same way.</summary>
public sealed class AppFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = (await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults("crawldad_test");

            builder.ConfigureServices(services =>
            {
                // Overrides the last TimeProvider registration so the Runs interpreter reads a frozen clock,
                // keeping run traces deterministic.
                services.AddSingleton<TimeProvider>(new FakeClock());
            });
        })).AuthenticatedAsPrimaryTenant();

        await Host.ResetAllMartenDataAsync();
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<AppFixture>
{
    public const string Name = "integration";
}
