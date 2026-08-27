using Alba;
using Crawldad.Api;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>A shared Alba host for the fixture record/replay suite (its own Marten schema). Default auth is the primary
/// tenant; isolation tests override per scenario with the secondary key. The <c>fake</c> storage provider backs the
/// record run's connect to the shipped <c>record-search-detail</c> "site" fixture.</summary>
public sealed class FixtureApiFixture : IAsyncLifetime
{
    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = (await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults("crawldad_fixtures");
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(new FakeClock()));
        })).AuthenticatedAsPrimaryTenant();
        await Host.ResetAllMartenDataAsync();
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();
}

/// <summary>The fixture-API collection — one shared host, sequential tests, each resetting Marten data first.</summary>
[CollectionDefinition(Name)]
public sealed class FixtureApiCollection : ICollectionFixture<FixtureApiFixture>
{
    public const string Name = "fixture-api";
}
