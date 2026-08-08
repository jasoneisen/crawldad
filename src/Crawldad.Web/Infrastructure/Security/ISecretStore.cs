using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Web.Infrastructure.Security;

/// <summary>
/// The credential-by-reference seam (§12): payloads and events carry a <em>reference</em> into a secret store, never
/// the raw secret. A backend adapter resolves the reference to a live secret <b>only at connect time</b>; the resolved
/// value lives solely in the interpreter's memory for the session and must never be persisted, logged, or placed in an
/// exception message. Phase 4 WP1 ships the minimal configuration-backed implementation; WP3 hardens this into a real
/// vault seam and layers the scrubbing filter at the logging/event/trace sinks.
/// <para>
/// CD-6 makes this a <b>keyed adapter</b> (the same pattern as browser backends and storage targets): the customer's
/// vault is the sole custodian, selected by kind through <see cref="ISecretStoreRegistry"/> — <c>config</c> today (secrets
/// from <see cref="IConfiguration"/>), a real <c>azure-keyvault</c>/<c>aws-secretsmanager</c>/<c>hashicorp-vault</c>/HTTP
/// adapter later (one <c>AddKeyedSingleton</c> line, no change here). Two resolution surfaces sit on the one adapter: the
/// backend-connect path (global, <see cref="ResolveAsync"/>) and the CD-6 form-fill secretRef path (tenant-scoped,
/// <see cref="ResolveForTenantAsync"/>), so a tenant can never resolve another tenant's form-fill reference.
/// </para>
/// </summary>
public interface ISecretStore
{
    /// <summary>Resolves <paramref name="credentialRef"/> to its secret value for a backend connect (§9.1) — process-global,
    /// not tenant-scoped (a backend credential is an operator-configured account credential).</summary>
    /// <param name="credentialRef">The reference id carried by the backend binding (never the secret itself).</param>
    /// <param name="ct">Cancels the resolution.</param>
    /// <returns>The resolved secret (an account token, an API key, or a whole one-time connect URL, §9.1).</returns>
    /// <exception cref="SecretNotFoundException">When no secret is stored for the reference.</exception>
    Task<string> ResolveAsync(string credentialRef, CancellationToken ct);

    /// <summary>
    /// Resolves a CD-6 form-fill <c>secretRef</c> to its secret value, <b>scoped to <paramref name="tenant"/></b>: the
    /// tenant comes from the authenticated principal (never payload data), so a tenant can only ever resolve its own
    /// references. The <c>config</c> adapter reads a per-tenant-namespaced key (<c>Secrets:{tenant}:{reference}</c>).
    /// </summary>
    /// <param name="reference">The secretRef input's value — the reference string only, never the secret.</param>
    /// <param name="tenant">The run's authenticated tenant (CD-1) the reference is resolved under.</param>
    /// <param name="ct">Cancels the resolution.</param>
    /// <returns>The resolved secret to type into the form field.</returns>
    /// <exception cref="SecretNotFoundException">When no secret is stored for the tenant's reference.</exception>
    Task<string> ResolveForTenantAsync(string reference, string tenant, CancellationToken ct);
}

/// <summary>The recognised secret-vault kinds (CD-6). Only <see cref="Config"/> ships; the keyed registry accommodates a
/// real <c>azure-keyvault</c>/<c>aws-secretsmanager</c>/<c>hashicorp-vault</c>/HTTP adapter later with no change to callers.</summary>
public static class SecretVaults
{
    /// <summary>The configuration-backed vault (the CD-6 default and only shipped adapter): secrets from <see cref="IConfiguration"/>.</summary>
    public const string Config = "config";
}

/// <summary>
/// The configuration-backed <see cref="ISecretStore"/> (CD-6 vault kind <c>config</c>): secrets live under the
/// <c>Secrets</c> configuration section, so any <see cref="IConfiguration"/> provider (user-secrets, env vars, a mounted
/// vault file) supplies them without code change. Two key layouts, one per resolution surface:
/// <list type="bullet">
///   <item><b>Backend connect</b> (<see cref="ResolveAsync"/>): <c>Secrets:{credentialRef}</c> — process-global, an
///   operator-configured account credential.</item>
///   <item><b>Form-fill secretRef</b> (<see cref="ResolveForTenantAsync"/>): <c>Secrets:{tenant}:{reference}</c> —
///   <b>namespaced per tenant</b>, so tenant A's key is unreachable when resolving under tenant B (CD-6/CD-1).</item>
/// </list>
/// The lookup itself never emits the secret; a miss reports only the <em>reference</em> (which is safe — it is not the secret).
/// </summary>
/// <param name="configuration">The host configuration the secrets section is read from.</param>
internal sealed class ConfigurationSecretStore(IConfiguration configuration) : ISecretStore
{
    /// <summary>The configuration section secrets are read from (<c>Secrets:{ref}</c> for connect, <c>Secrets:{tenant}:{ref}</c> for form-fill).</summary>
    internal const string Section = "Secrets";

    public Task<string> ResolveAsync(string credentialRef, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentialRef);
        var secret = configuration[$"{Section}:{credentialRef}"]
            ?? throw new SecretNotFoundException(credentialRef);
        return Task.FromResult(secret);
    }

    public Task<string> ResolveForTenantAsync(string reference, string tenant, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        ArgumentException.ThrowIfNullOrEmpty(tenant);
        // Tenant-namespaced key (CD-6): the tenant is the authenticated principal's, so a payload can never widen the
        // lookup to another tenant's reference. A miss names only the (safe) reference, never the tenant-qualified key.
        var secret = configuration[$"{Section}:{tenant}:{reference}"]
            ?? throw new SecretNotFoundException(reference);
        return Task.FromResult(secret);
    }
}

/// <summary>
/// Resolves a secret-vault <c>kind</c> to the <see cref="ISecretStore"/> adapter that handles it (CD-6) — the
/// credential-vault analogue of <c>IBrowserBackendRegistry</c>/<c>IDownloadSinkRegistry</c>. A <c>secretRef</c> selects its
/// vault by kind (data, not a hard-coded type; <c>config</c> today), so a real vault adapter is one <c>AddKeyedSingleton</c>
/// line with no change to the interpreter. An unknown kind is a terminal <c>unknown_secret_vault</c> failure.
/// </summary>
public interface ISecretStoreRegistry
{
    /// <summary>Resolves the vault adapter for <paramref name="vault"/>.</summary>
    /// <param name="vault">The vault kind (e.g. <see cref="SecretVaults.Config"/>).</param>
    /// <param name="store">The resolved vault adapter when the kind is registered.</param>
    /// <returns><see langword="true"/> when a vault is registered for <paramref name="vault"/>; otherwise <see langword="false"/>.</returns>
    bool TryResolve(string vault, [NotNullWhen(true)] out ISecretStore? store);
}

/// <summary>
/// Registry over .NET keyed services: each vault kind is a keyed <see cref="ISecretStore"/> (key = kind), so a real
/// CD-6 vault adapter is one <c>AddKeyedSingleton</c> line with no change here.
/// </summary>
/// <param name="services">The service provider the keyed vault adapters are resolved from.</param>
internal sealed class KeyedSecretStoreRegistry(IServiceProvider services) : ISecretStoreRegistry
{
    public bool TryResolve(string vault, [NotNullWhen(true)] out ISecretStore? store)
    {
        store = services.GetKeyedService<ISecretStore>(vault);
        return store is not null;
    }
}

/// <summary>
/// No secret is stored for a referenced credential. Carries only the <em>reference</em> (safe to surface — it is a
/// vault key, not the secret). Adapters convert this to a terminal <see cref="Browser.BrowserConnectException"/> so it
/// never surfaces raw, and by construction it never holds secret material.
/// </summary>
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
