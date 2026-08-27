using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The host's Data-Protection key-ring wiring. The browser credential store encrypts secrets at rest with a
/// purpose-bound protector, so the key ring must outlive the container: when <c>Crawldad:DataProtection</c> is configured
/// it is persisted to a blob and wrapped by a Key Vault key (managed-identity auth); absent config keeps the framework's
/// default local ring, so dev/tests are untouched. Mirrors <see cref="Storage.StorageModule"/>'s config-gated shape.</summary>
public static class DataProtectionModule
{
    /// <summary>The fixed Data-Protection application discriminator. Pinned so decryptability never rides on the
    /// container WORKDIR (the framework derives the default from the content-root path) — changing it once beta
    /// credentials are encrypted would break Unprotect at connect.</summary>
    internal const string ApplicationName = "crawldad";

    /// <summary>Registers Data Protection plus the key-ring options + boot guard, and — only when the section is fully
    /// configured — the Azure blob-persist + Key-Vault-wrap providers.</summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configuration">The host configuration (the key ring is read from <c>Crawldad:DataProtection</c>).</param>
    public static void AddKeyRingProtection(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The knobs + boot guard (a half-configured pair fails at startup rather than silently going ephemeral).
        services.AddOptions<DataProtectionOptions>().BindConfiguration(DataProtectionOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<DataProtectionOptions>, DataProtectionOptionsValidator>();

        var builder = services.AddDataProtection();

        // Pin the discriminator unconditionally (the local-default AND Azure-persisted rings), so a WORKDIR/content-root
        // change never silently shifts it and breaks Unprotect at connect. Free now — no durable ciphertext exists yet.
        builder.SetApplicationName(ApplicationName);

        // The wiring choice is a registration-time decision, so read the section directly (IOptions isn't available yet) —
        // the same indexer idiom StorageModule uses to select its provider.
        var blobUri = configuration[$"{DataProtectionOptions.Section}:KeyRingBlobUri"];
        var keyVaultKeyId = configuration[$"{DataProtectionOptions.Section}:KeyVaultKeyId"];
        if (string.IsNullOrWhiteSpace(blobUri) || string.IsNullOrWhiteSpace(keyVaultKeyId))
        {
            return; // no config → the default local ring; a half-set pair is rejected loudly by the boot validator
        }

        // Persist the whole ring to one blob and wrap each key with the Key Vault key, both via the app's managed
        // identity (DefaultAzureCredential resolves the user-assigned identity from AZURE_CLIENT_ID). Construction is
        // I/O-free — the ring is read only on the first protect/unprotect.
        var credential = new DefaultAzureCredential();
        builder
            .PersistKeysToAzureBlobStorage(new Uri(blobUri), credential)
            .ProtectKeysWithAzureKeyVault(new Uri(keyVaultKeyId), credential);
    }
}
