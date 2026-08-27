using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Portal.Infrastructure.Security;

/// <summary>The portal's Data-Protection key-ring persistence knobs, bound from <c>Crawldad:Portal:DataProtection</c>
/// for the boot-time guard (<see cref="DataProtectionOptionsValidator"/>). Both values set ⇒ the key ring is persisted
/// to the blob at <see cref="KeyRingBlobUri"/> and each key is wrapped by the Key Vault key at
/// <see cref="KeyVaultKeyId"/> (managed-identity auth). Both empty (the default) ⇒ the framework's default local
/// (container-ephemeral) key ring, so dev/tests are untouched. Half-configured fails fast at boot.
/// <see cref="DataProtectionModule"/> reads the pair to wire persistence. This is the portal's OWN section, distinct
/// from the API's <c>Crawldad:DataProtection</c>; the two hosts run as separate apps and persist to separate rings.</summary>
public sealed class DataProtectionOptions
{
    /// <summary>The configuration section these bind from. Portal-scoped (mirrors the API's <c>Crawldad:DataProtection</c>
    /// but under <c>Portal</c>) so the two rings' config never collides and the infra plumbing is self-documenting.</summary>
    public const string Section = "Crawldad:Portal:DataProtection";

    /// <summary>The absolute URI of the single blob the whole portal key ring is stored in (e.g.
    /// <c>https://acct.blob.core.windows.net/dataprotection-portal/keyring.xml</c>; the portal's own container, never
    /// the API's ring blob). Empty ⇒ persistence off.</summary>
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings",
        Justification = "A config-bound value parsed to System.Uri at wiring time; the host models config URIs as strings (the API's DataProtection seam does the same).")]
    public string KeyRingBlobUri { get; init; } = "";

    /// <summary>The Key Vault key identifier used to wrap/unwrap each key (e.g.
    /// <c>https://vault.vault.azure.net/keys/dataprotection-portal</c>; versionless so rotation keeps decrypting). Empty ⇒ off.</summary>
    public string KeyVaultKeyId { get; init; } = "";
}
