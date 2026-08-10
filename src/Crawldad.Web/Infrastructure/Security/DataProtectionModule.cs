using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Web.Infrastructure.Security;

/// <summary>The host's Data-Protection key-ring wiring. The browser credential store encrypts secrets at rest with a
/// purpose-bound protector, so the key ring must outlive the container: when <c>Crawldad:DataProtection</c> is configured
/// it is persisted to a blob and wrapped by a Key Vault key (managed-identity auth); absent config keeps the framework's
/// default local ring, so dev/tests are untouched. Mirrors <see cref="Storage.StorageModule"/>'s config-gated shape.</summary>
public static class DataProtectionModule
{
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

        // The wiring choice is a registration-time decision, so read the section directly (IOptions isn't available yet).
        var options = configuration.GetSection(DataProtectionOptions.Section).Get<DataProtectionOptions>() ?? new();
        if (!options.IsAzurePersisted)
        {
            return; // absent config → the framework's default local key ring, untouched
        }

        // Persist the whole ring to one blob and wrap each key with the Key Vault key, both via the app's managed
        // identity (DefaultAzureCredential resolves the user-assigned identity from AZURE_CLIENT_ID). Construction is
        // I/O-free — the ring is read only on the first protect/unprotect.
        var credential = new DefaultAzureCredential();
        builder
            .PersistKeysToAzureBlobStorage(new Uri(options.KeyRingBlobUri), credential)
            .ProtectKeysWithAzureKeyVault(new Uri(options.KeyVaultKeyId), credential);
    }
}
