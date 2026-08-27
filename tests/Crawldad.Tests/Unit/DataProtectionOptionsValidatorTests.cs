using Crawldad.Api.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>The boot-time key-ring guard (<see cref="DataProtectionOptionsValidator"/>): neither knob set (the dev/test
/// default) passes, both set as absolute URIs passes, and every half-configured or malformed shape fails startup with a
/// specific message — so a partially-configured host can never silently fall back to the ephemeral ring.</summary>
public class DataProtectionOptionsValidatorTests
{
    private const string _blob = "https://acct.blob.core.windows.net/dataprotection/keyring.xml";
    private const string _key = "https://kv-crawldad-stg.vault.azure.net/keys/dataprotection";

    private static readonly DataProtectionOptionsValidator _validator = new();

    private static ValidateOptionsResult Validate(DataProtectionOptions options) => _validator.Validate(name: null, options);

    [Fact]
    public void Neither_knob_set_is_valid_the_default_local_ring() =>
        Validate(new DataProtectionOptions()).Succeeded.ShouldBeTrue();

    [Fact]
    public void Both_knobs_set_as_absolute_uris_is_valid() =>
        Validate(new DataProtectionOptions { KeyRingBlobUri = _blob, KeyVaultKeyId = _key }).Succeeded.ShouldBeTrue();

    [Fact]
    public void Only_the_blob_uri_set_fails_both_or_neither()
    {
        var result = Validate(new DataProtectionOptions { KeyRingBlobUri = _blob });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("KeyRingBlobUri and KeyVaultKeyId");
    }

    [Fact]
    public void Only_the_key_id_set_fails_both_or_neither()
    {
        var result = Validate(new DataProtectionOptions { KeyVaultKeyId = _key });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("KeyRingBlobUri and KeyVaultKeyId");
    }

    [Fact]
    public void A_non_absolute_blob_uri_fails()
    {
        var result = Validate(new DataProtectionOptions { KeyRingBlobUri = "dataprotection/keyring.xml", KeyVaultKeyId = _key });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("KeyRingBlobUri must be an absolute URI");
    }

    [Fact]
    public void A_non_absolute_key_id_fails()
    {
        var result = Validate(new DataProtectionOptions { KeyRingBlobUri = _blob, KeyVaultKeyId = "not a uri" });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("KeyVaultKeyId must be an absolute URI");
    }

    [Fact]
    public void Null_options_is_rejected() =>
        Should.Throw<ArgumentNullException>(() => Validate(null!));
}
