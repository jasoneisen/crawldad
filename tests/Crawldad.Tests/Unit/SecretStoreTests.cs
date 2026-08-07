using System.Collections.Generic;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The credential-by-reference seam (§12): the configuration-backed <see cref="ConfigurationSecretStore"/> resolves a
/// reference to its <c>Secrets:{ref}</c> value, and a missing reference is a <see cref="SecretNotFoundException"/> that
/// names only the (safe) reference — never a secret.
/// </summary>
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
}
