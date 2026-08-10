using System.Collections.Generic;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>The Data-Protection key-ring wiring (<see cref="DataProtectionModule"/>): with no <c>Crawldad:DataProtection</c>
/// config the framework's default local ring stands (dev/tests untouched); with both the blob URI and the Key Vault key
/// id set, the ring is persisted to Azure blob storage and wrapped by the vault key. The Azure branch resolves without
/// any live Azure — construction is I/O-free (the ring is read only on the first protect/unprotect), so it is covered hermetically.</summary>
public class DataProtectionModuleTests
{
    private const string _blob = "https://acct.blob.core.windows.net/dataprotection/keyring.xml";
    private const string _key = "https://kv-crawldad-stg.vault.azure.net/keys/dataprotection";

    private static KeyManagementOptions Wire(string? blobUri, string? keyId)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (blobUri is not null)
        {
            settings["Crawldad:DataProtection:KeyRingBlobUri"] = blobUri;
        }

        if (keyId is not null)
        {
            settings["Crawldad:DataProtection:KeyVaultKeyId"] = keyId;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(); // the DP key-management options setup resolves ILoggerFactory
        services.AddSingleton<IConfiguration>(config);
        DataProtectionModule.AddKeyRingProtection(services, config);

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
        Should.Throw<ArgumentNullException>(() => DataProtectionModule.AddKeyRingProtection(null!, config));
        Should.Throw<ArgumentNullException>(() => DataProtectionModule.AddKeyRingProtection(services, null!));
    }
}
