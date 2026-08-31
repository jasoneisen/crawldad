using Azure.Identity;
using Crawldad.Portal.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Portal.Infrastructure.Security;

/// <summary>The portal's Data-Protection key-ring wiring. The portal protects its auth cookie (issued on the static-SSR
/// login POST) and antiforgery tokens at rest with this key ring. It must outlive the container, otherwise every
/// restart/replace rotates the ring and signs users out. (Issue #119 retired the stored-key path, so the ring no longer
/// protects any tenant API key — it backs cookies/antiforgery only now.) So when <c>Crawldad:Portal:DataProtection</c> is
/// configured the ring is persisted to a blob and wrapped by a Key Vault key (managed-identity auth); absent config keeps
/// the framework's default local ring, so dev/tests are untouched. Mirrors the API's
/// <c>Crawldad.Api.Infrastructure.Security.DataProtectionModule</c> — same config shape, same fail-closed/fallback semantics.</summary>
public static class DataProtectionModule
{
    /// <summary>The portal's fixed Data-Protection application discriminator. DELIBERATELY distinct from the API's
    /// <c>"crawldad"</c> (<c>Crawldad.Api.Infrastructure.Security.DataProtectionModule.ApplicationName</c>): the
    /// discriminator is folded into every key derivation, so even if the portal and the API were ever pointed at the
    /// SAME key ring, a payload protected by one could never be unprotected by the other. Combined with the portal's
    /// own key-ring blob (its own storage container, never the API's), the two apps' protected data (the portal's
    /// cookies/antiforgery, the API's browser/webhook secrets) is fully isolated. Pinned so decryptability never rides on
    /// the container WORKDIR (the framework otherwise derives the discriminator from the content-root path) — changing it
    /// once cookies are encrypted would break Unprotect.</summary>
    internal const string ApplicationName = "crawldad-portal";

    /// <summary>Registers Data Protection plus the key-ring options + boot guard, and — only when the section is fully
    /// configured — the Azure blob-persist + Key-Vault-wrap providers.</summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configuration">The host configuration (the key ring is read from <c>Crawldad:Portal:DataProtection</c>).</param>
    public static void AddKeyRingProtection(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The knobs + boot guard (a half-configured pair fails at startup rather than silently going ephemeral).
        services.AddOptions<DataProtectionOptions>().BindConfiguration(DataProtectionOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<DataProtectionOptions>, DataProtectionOptionsValidator>();

        var builder = services.AddDataProtection();

        // Pin the discriminator unconditionally (the local-default AND Azure-persisted rings), so a WORKDIR/content-root
        // change never silently shifts it and breaks Unprotect on an existing cookie / antiforgery token. This is also the
        // cryptographic isolation boundary from the API (see ApplicationName): its "crawldad" and the portal's
        // "crawldad-portal" derive different keys from the same material.
        builder.SetApplicationName(ApplicationName);

        // The wiring choice is a registration-time decision, so read the section directly (IOptions isn't available yet) —
        // the same indexer idiom the API's DataProtectionModule uses to select its provider.
        var blobUri = configuration[$"{DataProtectionOptions.Section}:KeyRingBlobUri"];
        var keyVaultKeyId = configuration[$"{DataProtectionOptions.Section}:KeyVaultKeyId"];
        if (string.IsNullOrWhiteSpace(blobUri) || string.IsNullOrWhiteSpace(keyVaultKeyId))
        {
            return; // no config → the default local ring; a half-set pair is rejected loudly by the boot validator
        }

        // Persist the whole ring to one blob (the portal's OWN container, never the API's ring) and wrap each key with
        // the Key Vault key, both via the app's managed identity (DefaultAzureCredential resolves the user-assigned
        // identity from AZURE_CLIENT_ID). Construction is I/O-free — the ring is read only on the first protect/unprotect.
        var credential = new DefaultAzureCredential();
        builder
            .PersistKeysToAzureBlobStorage(new Uri(blobUri), credential)
            .ProtectKeysWithAzureKeyVault(new Uri(keyVaultKeyId), credential);
    }
}
