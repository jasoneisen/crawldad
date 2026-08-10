using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Web.Infrastructure.Security;

/// <summary>The credential-by-reference seam: payloads/events carry a reference, never the raw secret, which is
/// resolved to a live value only at connect time and must never be persisted, logged, or placed in an exception. A
/// keyed adapter by vault kind; every surface is tenant-scoped so tenants can't reach each other's references.</summary>
public interface ISecretStore
{
    /// <summary>Resolves a <c>secretRef</c> to its value, scoped to <paramref name="tenant"/>: the tenant comes from the
    /// authenticated principal (never payload data), so a tenant can only resolve its own references. Backs both the
    /// form-fill surface and the backend-connect resolver's config fallback.</summary>
    /// <exception cref="SecretNotFoundException">When no secret is stored for the tenant's reference.</exception>
    Task<string> ResolveForTenantAsync(string reference, string tenant, CancellationToken ct);
}

/// <summary>The recognised secret-vault kinds. Only <see cref="Config"/> ships; the keyed registry accommodates a
/// real <c>azure-keyvault</c>/<c>aws-secretsmanager</c>/<c>hashicorp-vault</c>/HTTP adapter later with no change to callers.</summary>
public static class SecretVaults
{
    /// <summary>The configuration-backed vault (the default and only shipped adapter): secrets from <see cref="IConfiguration"/>.</summary>
    public const string Config = "config";
}

/// <summary>The configuration-backed <see cref="ISecretStore"/>: secrets live under the <c>Secrets</c> configuration
/// section, always namespaced per tenant — <c>Secrets:{tenant}:{reference}</c> — so tenant A's key is unreachable when
/// resolving under tenant B. There is no flat <c>Secrets:{ref}</c> read; a connect ref falls back here tenant-scoped.</summary>
internal sealed class ConfigurationSecretStore(IConfiguration configuration) : ISecretStore
{
    /// <summary>The configuration section secrets are read from (<c>Secrets:{tenant}:{ref}</c>, tenant-namespaced).</summary>
    internal const string Section = "Secrets";

    public Task<string> ResolveForTenantAsync(string reference, string tenant, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        ArgumentException.ThrowIfNullOrEmpty(tenant);
        // Tenant-namespaced key: the tenant is the authenticated principal's, so a payload can never widen the
        // lookup to another tenant's reference. A miss names only the (safe) reference, never the tenant-qualified key.
        var secret = configuration[$"{Section}:{tenant}:{reference}"]
            ?? throw new SecretNotFoundException(reference);
        return Task.FromResult(secret);
    }
}

/// <summary>Resolves a secret-vault <c>kind</c> to the <see cref="ISecretStore"/> adapter that handles it. A
/// <c>secretRef</c> selects its vault by kind (data, not a hard-coded type), so a real vault adapter is one
/// registration away. An unknown kind is a terminal <c>unknown_secret_vault</c> failure.</summary>
public interface ISecretStoreRegistry
{
    /// <summary>Resolves the vault adapter for <paramref name="vault"/>; <see langword="true"/> when one is registered.</summary>
    bool TryResolve(string vault, [NotNullWhen(true)] out ISecretStore? store);
}

/// <summary>Registry over .NET keyed services: each vault kind is a keyed <see cref="ISecretStore"/> (key = kind), so
/// a real vault adapter is one <c>AddKeyedSingleton</c> line with no change here.</summary>
internal sealed class KeyedSecretStoreRegistry(IServiceProvider services) : ISecretStoreRegistry
{
    public bool TryResolve(string vault, [NotNullWhen(true)] out ISecretStore? store)
    {
        store = services.GetKeyedService<ISecretStore>(vault);
        return store is not null;
    }
}

/// <summary>No secret is stored for a referenced credential. Carries only the reference (safe to surface — a vault
/// key, not the secret); adapters convert this to a terminal <see cref="Browser.BrowserConnectException"/> so it never
/// surfaces raw.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "The reference is mandatory so the fault always names which credential was missing; a parameterless constructor would allow a referenceless miss.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
public sealed class SecretNotFoundException : Exception
{
    /// <summary>Creates a missing-secret fault naming the (safe) reference.</summary>
    /// <param name="credentialRef">The unresolved reference id — never the secret.</param>
    public SecretNotFoundException(string credentialRef)
        : base($"no secret is configured for credential reference '{credentialRef}'") => CredentialRef = credentialRef;

    /// <summary>The reference that could not be resolved (safe to surface — it is not the secret).</summary>
    public string CredentialRef { get; }
}
