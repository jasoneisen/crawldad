using Microsoft.Extensions.Options;

namespace Crawldad.Web.Infrastructure.Security;

/// <summary>The boot-time guard for the key-ring knobs (bound from <c>Crawldad:DataProtection</c>, registered with
/// <c>ValidateOnStart</c>). It rejects a half-configured pair — one of the two set without the other — because that
/// would silently fall back to the ephemeral key ring and re-introduce the exact undecryptable-after-redeploy bug this
/// section fixes. When both are set they must be absolute URIs.</summary>
public sealed class DataProtectionOptionsValidator : IValidateOptions<DataProtectionOptions>
{
    /// <summary>Validates the bound key-ring knobs, collecting every failure so a misconfigured host reports them together.</summary>
    public ValidateOptionsResult Validate(string? name, DataProtectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var hasBlob = !string.IsNullOrWhiteSpace(options.KeyRingBlobUri);
        var hasKey = !string.IsNullOrWhiteSpace(options.KeyVaultKeyId);
        var failures = new List<string>();

        if (hasBlob != hasKey)
        {
            failures.Add("Crawldad:DataProtection needs BOTH KeyRingBlobUri and KeyVaultKeyId set (or neither)");
        }

        RequireAbsoluteUri(failures, hasBlob, options.KeyRingBlobUri, "KeyRingBlobUri");
        RequireAbsoluteUri(failures, hasKey, options.KeyVaultKeyId, "KeyVaultKeyId");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequireAbsoluteUri(List<string> failures, bool present, string value, string knob)
    {
        if (present && !Uri.IsWellFormedUriString(value, UriKind.Absolute))
        {
            failures.Add($"Crawldad:DataProtection:{knob} must be an absolute URI");
        }
    }
}
