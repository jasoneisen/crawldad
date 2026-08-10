using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Web.Infrastructure.Security;

/// <summary>The Data-Protection key-ring persistence knobs, bound from <c>Crawldad:DataProtection</c> for the boot-time
/// guard (<see cref="DataProtectionOptionsValidator"/>). Both values set ⇒ the key ring is persisted to the blob at
/// <see cref="KeyRingBlobUri"/> and each key is wrapped by the Key Vault key at <see cref="KeyVaultKeyId"/>
/// (managed-identity auth). Both empty (the default) ⇒ the framework's default local key ring, so dev/tests are
/// untouched. Half-configured fails fast at boot. <see cref="DataProtectionModule"/> reads the pair to wire persistence.</summary>
public sealed class DataProtectionOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string Section = "Crawldad:DataProtection";

    /// <summary>The absolute URI of the single blob the whole key ring is stored in (e.g.
    /// <c>https://acct.blob.core.windows.net/dataprotection/keyring.xml</c>). Empty ⇒ persistence off.</summary>
    [SuppressMessage("Design", "CA1056:URI-like properties should not be strings",
        Justification = "A config-bound value parsed to System.Uri at wiring time; the host models config URIs as strings (storage/browser seams do the same).")]
    public string KeyRingBlobUri { get; init; } = "";

    /// <summary>The Key Vault key identifier used to wrap/unwrap each key (e.g.
    /// <c>https://vault.vault.azure.net/keys/dataprotection</c>; versionless so rotation keeps decrypting). Empty ⇒ off.</summary>
    public string KeyVaultKeyId { get; init; } = "";
}
