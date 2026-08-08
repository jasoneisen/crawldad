namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>
/// Shared, traversal-safe blob naming for the durable adapters (CD-2), used by <b>both</b> the filesystem and Azure stores so
/// the per-tenant isolation guard lives in one covered place — never duplicated, and never hidden inside the
/// <c>[ExcludeFromCodeCoverage]</c> Azure adapter. The tenant (and any key) is the one attacker-influenceable path segment (a
/// tenant id is only length-validated upstream), so a <c>/</c>- or <c>..</c>-bearing value could otherwise collapse prefixes
/// and read/overwrite another tenant's blobs; <see cref="SafeSegment"/> rejects that. The content id (a GUID) and screenshot
/// key (a hex digest) are intrinsically safe but are still routed through the guard so every adapter builds names identically.
/// </summary>
internal static class BlobNaming
{
    /// <summary>The download category's sub-path (a tenant's attachments).</summary>
    public const string DownloadsDir = "downloads";

    /// <summary>The screenshot category's sub-path (a tenant's failure screenshots).</summary>
    public const string ScreenshotsDir = "screenshots";

    private static readonly char[] _separators = ['/', '\\'];

    /// <summary>The sub-path for a blob category.</summary>
    /// <param name="kind">The blob category.</param>
    /// <returns><see cref="DownloadsDir"/> or <see cref="ScreenshotsDir"/>.</returns>
    public static string SubDir(BlobKind kind) => kind == BlobKind.Download ? DownloadsDir : ScreenshotsDir;

    /// <summary>
    /// Validates one path/blob-name segment (a tenant or a key) against traversal: it must be non-empty and contain no path
    /// separator (<c>/</c> or <c>\</c>) and no <c>..</c>. Returns the value unchanged when safe; throws otherwise, so a
    /// hostile tenant id fails closed before any storage operation.
    /// </summary>
    /// <param name="value">The segment to validate (a tenant id, or a blob key).</param>
    /// <returns>The same value when it is a safe segment.</returns>
    /// <exception cref="ArgumentException">The value is empty, contains a separator, or contains <c>..</c>.</exception>
    public static string SafeSegment(string value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Contains("..", StringComparison.Ordinal)
            || value.IndexOfAny(_separators) >= 0)
        {
            throw new ArgumentException($"'{value}' is not a safe blob path segment", nameof(value));
        }

        return value;
    }
}
