using System.Collections.Generic;
using Crawldad.Contracts.Browsers;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Browsers;
using Crawldad.Web.Infrastructure.Security;

namespace Crawldad.Tests.Unit;

/// <summary>The tenant-scoped connect resolver's precedence: a registered browser wins, else the tenant-namespaced
/// config fallback, else a classified <see cref="SecretNotFoundException"/> — the same miss whether the ref belongs to
/// another tenant or nobody (no existence oracle). Backends resolve every connect credential through this.</summary>
public class BrowserCredentialResolverTests
{
    // A store returning a fixed secret (or null when unregistered); only TryResolveSecretAsync is exercised here.
    private sealed class StubStore(string? secret) : IBrowserCredentialStore
    {
        public Task<string?> TryResolveSecretAsync(string tenant, string name, CancellationToken ct) => Task.FromResult(secret);

        public Task<BrowserSummary> RegisterAsync(string tenant, string name, string adapter, string mode, string secret,
            IReadOnlyDictionary<string, string>? options, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<BrowserSummary>> ListAsync(string tenant, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string tenant, string name, CancellationToken ct) => throw new NotSupportedException();
    }

    private static BrowserCredentialResolver Resolver(string? registered, params (string Key, string Value)[] config) =>
        new(new StubStore(registered), new MapSecretStore(config.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal)));

    [Fact]
    public async Task A_registered_browser_wins_over_the_config_fallback()
    {
        var resolver = Resolver("registered-secret", ("prod", "config-secret"));
        (await resolver.ResolveConnectAsync("prod", "acme", CancellationToken.None)).ShouldBe("registered-secret");
    }

    [Fact]
    public async Task Falls_back_to_the_tenant_config_when_unregistered()
    {
        var resolver = Resolver(registered: null, ("prod", "config-secret"));
        (await resolver.ResolveConnectAsync("prod", "acme", CancellationToken.None)).ShouldBe("config-secret");
    }

    [Fact]
    public async Task A_total_miss_is_a_classified_secret_not_found_naming_only_the_ref()
    {
        var resolver = Resolver(registered: null); // neither registered nor in config
        var ex = await Should.ThrowAsync<SecretNotFoundException>(
            () => resolver.ResolveConnectAsync("prod", "acme", CancellationToken.None));
        ex.CredentialRef.ShouldBe("prod");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Rejects_an_empty_ref_or_tenant(string? bad)
    {
        var resolver = Resolver("s");
        await Should.ThrowAsync<ArgumentException>(() => resolver.ResolveConnectAsync(bad!, "acme", CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => resolver.ResolveConnectAsync("prod", bad!, CancellationToken.None));
    }
}
