using System.Collections.Generic;
using Crawldad.Api.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Tests.Unit;

/// <summary>The credential-by-reference seam: the configuration-backed <see cref="ConfigurationSecretStore"/> resolves a
/// reference to its tenant-namespaced <c>Secrets:{tenant}:{ref}</c> value, and a miss is a <see cref="SecretNotFoundException"/>
/// naming only the (safe) reference — never the secret, never the tenant-qualified key. There is no flat, process-global read.</summary>
public class SecretStoreTests
{
    private static ConfigurationSecretStore TenantStore(params (string Key, string Value)[] secrets)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(secrets.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new ConfigurationSecretStore(config);
    }

    [Fact]
    public async Task Resolves_a_secret_under_its_tenant_namespace()
    {
        var store = TenantStore(("Secrets:acme:login-password", "hunter2"));
        (await store.ResolveForTenantAsync("login-password", "acme", CancellationToken.None)).ShouldBe("hunter2");
    }

    [Fact]
    public async Task A_tenant_cannot_resolve_another_tenants_reference()
    {
        // Only tenant `acme` has a value for `login-password`; tenant `globex` resolving the same reference misses:
        // the config key is namespaced per tenant, so a tenant is structurally confined to its own references.
        var store = TenantStore(("Secrets:acme:login-password", "acme-secret"));

        (await store.ResolveForTenantAsync("login-password", "acme", CancellationToken.None)).ShouldBe("acme-secret");
        var ex = await Should.ThrowAsync<SecretNotFoundException>(
            () => store.ResolveForTenantAsync("login-password", "globex", CancellationToken.None));
        ex.CredentialRef.ShouldBe("login-password"); // names only the safe reference, never the tenant-qualified key
        ex.Message.ShouldContain("login-password");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_missing_reference_or_tenant_is_rejected(string? bad)
    {
        var store = TenantStore(("Secrets:acme:present", "v"));
        await Should.ThrowAsync<ArgumentException>(() => store.ResolveForTenantAsync(bad!, "acme", CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => store.ResolveForTenantAsync("present", bad!, CancellationToken.None));
    }
}
