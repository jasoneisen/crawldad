using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Crawldad.Portal.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ApiDataProtectionModule = Crawldad.Api.Infrastructure.Security.DataProtectionModule;
using AspNetDataProtectionOptions = Microsoft.AspNetCore.DataProtection.DataProtectionOptions;
using PortalDataProtectionModule = Crawldad.Portal.Infrastructure.Security.DataProtectionModule;

namespace Crawldad.Tests.Portal;

/// <summary>The portal's Data-Protection key-ring wiring (<c>Crawldad.Portal.Infrastructure.Security.DataProtectionModule</c>):
/// with no <c>Crawldad:Portal:DataProtection</c> config the framework's default local ring stands (dev/tests untouched);
/// with both the blob URI and the Key Vault key id set, the ring is persisted to Azure blob storage and wrapped by the
/// vault key. The Azure branch resolves without any live Azure — construction is I/O-free (the ring is read only on the
/// first protect/unprotect), so it is covered hermetically. The final test proves the portal's application discriminator
/// isolates its protected data from the API's, even over a shared key ring.</summary>
public class DataProtectionModuleTests
{
    private const string _blob = "https://acct.blob.core.windows.net/dataprotection-portal/keyring.xml";
    private const string _key = "https://kv-crawldad-stg.vault.azure.net/keys/dataprotection-portal";

    private static KeyManagementOptions Wire(string? blobUri, string? keyId)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (blobUri is not null)
        {
            settings["Crawldad:Portal:DataProtection:KeyRingBlobUri"] = blobUri;
        }

        if (keyId is not null)
        {
            settings["Crawldad:Portal:DataProtection:KeyVaultKeyId"] = keyId;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(); // the DP key-management options setup resolves ILoggerFactory
        services.AddSingleton<IConfiguration>(config);
        PortalDataProtectionModule.AddKeyRingProtection(services, config);

        using var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
    }

    [Fact]
    public void No_config_leaves_the_default_local_key_ring()
    {
        var options = Wire(blobUri: null, keyId: null);

        options.XmlRepository.ShouldBeNull();  // no Azure blob repository → the framework's default storage
        options.XmlEncryptor.ShouldBeNull();   // no Key Vault wrap → the framework's default key protection
    }

    [Fact]
    public void Both_knobs_set_persist_to_blob_and_wrap_with_the_vault_key()
    {
        var options = Wire(_blob, _key);

        options.XmlRepository.ShouldNotBeNull();
        options.XmlRepository.GetType().Name.ShouldContain("Blob");         // persisted to Azure blob storage
        options.XmlEncryptor.ShouldNotBeNull();
        options.XmlEncryptor.GetType().Name.ShouldContain("KeyVault");     // each key wrapped by the vault key
    }

    [Fact]
    public void Pins_a_fixed_application_discriminator_so_the_ring_survives_a_workdir_change()
    {
        // Unconditional — needs no DataProtection config; the default discriminator otherwise derives from the
        // content-root path, so a WORKDIR change would silently break Unprotect on an already-issued cookie or a
        // stored tenant key.
        var services = new ServiceCollection();
        services.AddLogging();
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);
        PortalDataProtectionModule.AddKeyRingProtection(services, config);

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IOptions<AspNetDataProtectionOptions>>().Value
            .ApplicationDiscriminator.ShouldBe(PortalDataProtectionModule.ApplicationName);
    }

    [Fact]
    public void A_half_configured_pair_does_not_wire_azure_persistence()
    {
        // Only the blob is set: the registration gate is both-present, so nothing Azure is wired (the boot-time
        // validator is what turns this misconfiguration into a loud startup failure — covered in its own tests).
        var options = Wire(_blob, keyId: null);

        options.XmlRepository.ShouldBeNull();
        options.XmlEncryptor.ShouldBeNull();
    }

    [Fact]
    public void AddKeyRingProtection_rejects_null_arguments()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        Should.Throw<ArgumentNullException>(() => PortalDataProtectionModule.AddKeyRingProtection(null!, config));
        Should.Throw<ArgumentNullException>(() => PortalDataProtectionModule.AddKeyRingProtection(services, null!));
    }

    [Fact]
    public void Portal_and_api_discriminators_are_distinct_so_neither_can_decrypt_the_others_payload()
    {
        // The discriminators are pinned to different constants...
        PortalDataProtectionModule.ApplicationName.ShouldBe("crawldad-portal");
        PortalDataProtectionModule.ApplicationName.ShouldNotBe(ApiDataProtectionModule.ApplicationName);

        // ...and that difference is a hard cryptographic boundary: Data Protection folds the application discriminator
        // into key derivation, so even over ONE shared key ring a payload sealed under the portal's discriminator
        // cannot be opened under the API's. (In production the two also persist to separate blobs — this proves the
        // discriminator alone already isolates them, which is the belt to that braces.)
        var keyRing = Directory.CreateTempSubdirectory("issue119dp");
        try
        {
            var portal = SharedRingProvider(PortalDataProtectionModule.ApplicationName, keyRing.FullName);
            var api = SharedRingProvider(ApiDataProtectionModule.ApplicationName, keyRing.FullName);

            var sealedByPortal = portal.CreateProtector(PortalTenancy.ApiKeyProtectorPurpose).Protect("tenant-api-key");

            // Same discriminator round-trips (the ring is functional)...
            portal.CreateProtector(PortalTenancy.ApiKeyProtectorPurpose).Unprotect(sealedByPortal).ShouldBe("tenant-api-key");
            // ...the API's discriminator cannot open it, despite sharing the exact same key material.
            Should.Throw<CryptographicException>(() =>
                api.CreateProtector(PortalTenancy.ApiKeyProtectorPurpose).Unprotect(sealedByPortal));
        }
        finally
        {
            keyRing.Delete(recursive: true);
        }
    }

    // A provider over a shared on-disk key ring, differing only by application discriminator — the one variable this
    // isolation test holds everything else constant against.
    private static IDataProtectionProvider SharedRingProvider(string applicationName, string keyRingDir)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingDir));
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }
}
