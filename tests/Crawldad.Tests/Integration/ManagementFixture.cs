using Alba;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>An Alba host with the interim management surface enabled (a configured <c>Management:ApiKey</c>) and the
/// registry key env label pinned, on its own Marten schema. Shared by the management + registry-auth suite. The env
/// tenants are still configured (via the test defaults), so the env-fallback path is exercised on this host too.</summary>
public sealed class ManagementFixture : IAsyncLifetime
{
    /// <summary>The configured management key these tests present (synthetic — never a real credential).</summary>
    public const string ManagementKey = "mgmt-key-SYNTHETIC-0123456789abcdef";

    /// <summary>The env label embedded in every key this host mints (<c>ck_test_…</c>).</summary>
    public const string KeyEnvLabel = "test";

    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseCrawldadTestDefaults("crawldad_mgmt_test");
            builder.UseSetting($"{ManagementOptions.Section}:ApiKey", ManagementKey);
            builder.UseSetting($"{TenantRegistryOptions.Section}:KeyEnvironmentLabel", KeyEnvLabel);

            // A frozen clock keeps issued-at/last-used timestamps deterministic; the auth cache is then driven purely by
            // explicit invalidation (revoke/suspend), which is exactly the revocation-safety these tests assert.
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(new FakeClock()));
        });

        await Host.ResetAllMartenDataAsync();
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class ManagementCollection : ICollectionFixture<ManagementFixture>
{
    public const string Name = "management";
}
