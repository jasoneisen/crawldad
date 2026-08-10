using System.Text.Json;
using Alba;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Browsers;
using Crawldad.Web.Infrastructure.Security;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>Tenant-scoped connect resolution over the real store + config vault: a registered browser resolves for its
/// tenant and beats the config fallback; a cross-tenant ref is a classified secret-not-found (no existence oracle); and
/// the endpoints never surface or mutate another tenant's registrations.</summary>
[Collection(BrowserApiCollection.Name)]
public sealed class BrowserResolutionTests(BrowserApiFixture fixture) : IAsyncLifetime
{
    private static readonly CancellationToken _ct = CancellationToken.None;
    private IAlbaHost Host => fixture.Host;
    private IConnectCredentialResolver Resolver => Host.Services.GetRequiredService<IConnectCredentialResolver>();
    private IBrowserCredentialStore Store => Host.Services.GetRequiredService<IBrowserCredentialStore>();

    public Task InitializeAsync() => Host.ResetAllMartenDataAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_registered_browser_resolves_for_its_tenant()
    {
        await Store.RegisterAsync(TestTenants.PrimaryId, "prod", "browserbase", "apiKey", "registered-secret", null, _ct);
        (await Resolver.ResolveConnectAsync("prod", TestTenants.PrimaryId, _ct)).ShouldBe("registered-secret");
    }

    [Fact]
    public async Task Another_tenant_cannot_resolve_a_registered_browser()
    {
        await Store.RegisterAsync(TestTenants.PrimaryId, "prod", "browserbase", "apiKey", "registered-secret", null, _ct);

        // Tenant B has no "prod" registered and no config for it: a classified miss, indistinguishable from a truly-absent ref.
        var ex = await Should.ThrowAsync<SecretNotFoundException>(
            () => Resolver.ResolveConnectAsync("prod", TestTenants.SecondaryId, _ct));
        ex.CredentialRef.ShouldBe("prod");
    }

    [Fact]
    public async Task Falls_back_to_the_tenant_config_vault()
    {
        (await Resolver.ResolveConnectAsync(BrowserApiFixture.ConfigFallbackRef, TestTenants.PrimaryId, _ct))
            .ShouldBe(BrowserApiFixture.ConfigFallbackSecret);
    }

    [Fact]
    public async Task Another_tenant_cannot_reach_the_config_fallback() =>
        await Should.ThrowAsync<SecretNotFoundException>(
            () => Resolver.ResolveConnectAsync(BrowserApiFixture.ConfigFallbackRef, TestTenants.SecondaryId, _ct));

    [Fact]
    public async Task A_registered_browser_beats_the_config_fallback()
    {
        // The same ref exists in BOTH the tenant config vault and as a registered browser; the registration wins.
        await Store.RegisterAsync(TestTenants.PrimaryId, BrowserApiFixture.ConfigFallbackRef, "browserless", "apiKey", "registered-wins", null, _ct);
        (await Resolver.ResolveConnectAsync(BrowserApiFixture.ConfigFallbackRef, TestTenants.PrimaryId, _ct)).ShouldBe("registered-wins");
    }

    [Fact]
    public async Task Tenant_B_does_not_see_tenant_As_browsers()
    {
        await Store.RegisterAsync(TestTenants.PrimaryId, "shared", "browserbase", "apiKey", "a-secret", null, _ct);

        var result = await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.SecondaryKey));
            x.Get.Url("/browsers");
            x.StatusCodeShouldBe(200);
        });
        (await result.ReadAsJsonAsync<JsonElement>()).GetProperty("browsers").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Tenant_B_cannot_delete_tenant_As_browser()
    {
        await Store.RegisterAsync(TestTenants.PrimaryId, "shared", "browserbase", "apiKey", "a-secret", null, _ct);

        await Host.Scenario(x =>
        {
            x.WithRequestHeader("Authorization", TestTenants.Bearer(TestTenants.SecondaryKey));
            x.Delete.Url("/browsers/shared");
            x.StatusCodeShouldBe(404); // absent in B's partition — no oracle
        });

        (await Store.TryResolveSecretAsync(TestTenants.PrimaryId, "shared", _ct)).ShouldBe("a-secret"); // A's is untouched
    }
}
