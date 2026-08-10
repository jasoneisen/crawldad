using System.Collections.Generic;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Tests.Unit;

/// <summary>The credential-by-reference seam: the configuration-backed <see cref="ConfigurationSecretStore"/> resolves a
/// reference to its <c>Secrets:{ref}</c> value, and a missing reference is a <see cref="SecretNotFoundException"/> that names only the (safe) reference — never a secret.</summary>
public class SecretStoreTests
{
    private static ConfigurationSecretStore Store(params (string Key, string Value)[] secrets)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(secrets.Select(s => new KeyValuePair<string, string?>($"Secrets:{s.Key}", s.Value)))
            .Build();
        return new ConfigurationSecretStore(config);
    }

    [Fact]
    public async Task Resolves_a_configured_secret()
    {
        var store = Store(("browserless-token", "tok_abc123"));
        (await store.ResolveAsync("browserless-token", CancellationToken.None)).ShouldBe("tok_abc123");
    }

    [Fact]
    public async Task A_missing_reference_throws_naming_only_the_reference()
    {
        var store = Store(("present", "v"));
        var ex = await Should.ThrowAsync<SecretNotFoundException>(
            () => store.ResolveAsync("absent-ref", CancellationToken.None));
        ex.CredentialRef.ShouldBe("absent-ref");
        ex.Message.ShouldContain("absent-ref");
    }

    [Fact]
    public async Task A_null_reference_is_rejected()
    {
        var store = Store();
        await Should.ThrowAsync<ArgumentNullException>(() => store.ResolveAsync(null!, CancellationToken.None));
    }

    // ----- form-fill secretRef resolution: tenant-scoped (Secrets:{tenant}:{ref}) -----

    private static ConfigurationSecretStore TenantStore(params (string Key, string Value)[] secrets)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(secrets.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new ConfigurationSecretStore(config);
    }

    [Fact]
    public async Task Resolves_a_form_fill_secret_under_its_tenant_namespace()
    {
        var store = TenantStore(("Secrets:acme:login-password", "hunter2"));
        (await store.ResolveForTenantAsync("login-password", "acme", CancellationToken.None)).ShouldBe("hunter2");
    }

    [Fact]
    public async Task A_tenant_cannot_resolve_another_tenants_form_fill_reference()
    {
        // Only tenant `acme` has a value for `login-password`; tenant `globex` resolving the same reference misses:
        // the config key is namespaced per tenant, so a tenant is structurally confined to its own references.
        var store = TenantStore(("Secrets:acme:login-password", "acme-secret"));

        (await store.ResolveForTenantAsync("login-password", "acme", CancellationToken.None)).ShouldBe("acme-secret");
        var ex = await Should.ThrowAsync<SecretNotFoundException>(
            () => store.ResolveForTenantAsync("login-password", "globex", CancellationToken.None));
        ex.CredentialRef.ShouldBe("login-password"); // names only the safe reference, never the tenant-qualified key
    }

    [Fact]
    public async Task A_form_fill_and_a_connect_secret_of_the_same_name_do_not_collide()
    {
        // The connect path reads Secrets:{ref} (global); the form-fill path reads Secrets:{tenant}:{ref} — distinct keys.
        var store = TenantStore(("Secrets:shared", "connect-value"), ("Secrets:acme:shared", "fill-value"));

        (await store.ResolveAsync("shared", CancellationToken.None)).ShouldBe("connect-value");
        (await store.ResolveForTenantAsync("shared", "acme", CancellationToken.None)).ShouldBe("fill-value");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_missing_form_fill_reference_or_tenant_is_rejected(string? bad)
    {
        var store = TenantStore(("Secrets:acme:present", "v"));
        await Should.ThrowAsync<ArgumentException>(() => store.ResolveForTenantAsync(bad!, "acme", CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => store.ResolveForTenantAsync("present", bad!, CancellationToken.None));
    }
}
