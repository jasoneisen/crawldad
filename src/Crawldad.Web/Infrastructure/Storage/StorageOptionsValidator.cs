using Microsoft.Extensions.Options;

namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>
/// The boot-time guard for the durable-storage knobs (CD-2), mirroring <c>RunLimitsOptionsValidator</c>: bound from
/// <c>Crawldad:Storage</c> and registered with <c>ValidateOnStart</c>, it fails the host <b>loudly at startup</b> on a
/// misconfiguration that would otherwise surface as a broken sweep (a non-positive interval a periodic timer can't run) or a
/// silently non-durable production store (a filesystem provider with no root, an Azure provider with no connection string).
/// The <see cref="StorageOptions.Provider"/> value itself is validated where it is switched on (an unknown provider throws at
/// registration); this closes the remaining gaps in the bound settings.
/// </summary>
public sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    /// <summary>Validates the bound storage knobs, collecting every failure so a misconfigured host reports them all at once.</summary>
    /// <param name="name">The options name (unused — there is a single unnamed instance).</param>
    /// <param name="options">The bound options.</param>
    /// <returns>Success, or a failure listing each invalid knob.</returns>
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        Require(failures, options.Retention.SweepInterval > TimeSpan.Zero, "Retention:SweepInterval", "a positive duration");
        Require(failures, options.Retention.DownloadTtl >= TimeSpan.Zero, "Retention:DownloadTtl", "0 or greater (0 disables the sweep)");
        Require(failures, options.Retention.ScreenshotTtl >= TimeSpan.Zero, "Retention:ScreenshotTtl", "0 or greater (0 disables the sweep)");

        if (string.Equals(options.Provider, StorageOptions.FileSystemProvider, StringComparison.Ordinal))
        {
            Require(failures, !string.IsNullOrWhiteSpace(options.FileSystem.Root), "FileSystem:Root", "a non-empty path");
        }

        if (string.Equals(options.Provider, StorageOptions.AzureProvider, StringComparison.Ordinal))
        {
            Require(failures, !string.IsNullOrWhiteSpace(options.Azure.ConnectionString), "Azure:ConnectionString", "a non-empty connection string");
            Require(failures, !string.IsNullOrWhiteSpace(options.Azure.Container), "Azure:Container", "a non-empty container name");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(List<string> failures, bool ok, string knob, string expectation)
    {
        if (!ok)
        {
            failures.Add($"Crawldad:Storage:{knob} must be {expectation}");
        }
    }
}
