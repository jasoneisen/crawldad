using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;

namespace Crawldad.Web.Infrastructure.Security;

/// <summary>
/// The credential-by-reference seam (§12): payloads and events carry a <em>reference</em> into a secret store, never
/// the raw secret. A backend adapter resolves the reference to a live secret <b>only at connect time</b>; the resolved
/// value lives solely in the interpreter's memory for the session and must never be persisted, logged, or placed in an
/// exception message. Phase 4 WP1 ships the minimal configuration-backed implementation; WP3 hardens this into a real
/// vault seam and layers the scrubbing filter at the logging/event/trace sinks.
/// </summary>
public interface ISecretStore
{
    /// <summary>Resolves <paramref name="credentialRef"/> to its secret value.</summary>
    /// <param name="credentialRef">The reference id carried by the backend binding (never the secret itself).</param>
    /// <param name="ct">Cancels the resolution.</param>
    /// <returns>The resolved secret (an account token, an API key, or a whole one-time connect URL, §9.1).</returns>
    /// <exception cref="SecretNotFoundException">When no secret is stored for the reference.</exception>
    Task<string> ResolveAsync(string credentialRef, CancellationToken ct);
}

/// <summary>
/// A configuration-backed <see cref="ISecretStore"/>: secrets live under the <c>Secrets</c> configuration section, keyed
/// by reference (<c>Secrets:{credentialRef}</c>), so any <see cref="IConfiguration"/> provider (user-secrets, env vars,
/// a mounted vault file) supplies them without code change. The lookup itself never emits the secret; a miss reports
/// only the <em>reference</em> (which is safe — it is not the secret).
/// </summary>
/// <param name="configuration">The host configuration the secrets section is read from.</param>
internal sealed class ConfigurationSecretStore(IConfiguration configuration) : ISecretStore
{
    /// <summary>The configuration section secrets are read from (<c>Secrets:{ref}</c>).</summary>
    internal const string Section = "Secrets";

    public Task<string> ResolveAsync(string credentialRef, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentialRef);
        var secret = configuration[$"{Section}:{credentialRef}"]
            ?? throw new SecretNotFoundException(credentialRef);
        return Task.FromResult(secret);
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
