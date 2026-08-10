namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>Shared, traversal-safe blob naming for the durable adapters, used by both the filesystem and Azure stores
/// so the per-tenant isolation guard lives in one covered place. The tenant is the one attacker-influenceable path
/// segment; <see cref="SafeSegment"/> rejects a <c>/</c>- or <c>..</c>-bearing value that could collapse prefixes.</summary>
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

    /// <summary>Validates one path/blob-name segment (a tenant or a key) against traversal: non-empty, no path
    /// separator, and no <c>..</c>. Returns the value unchanged when safe.</summary>
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
