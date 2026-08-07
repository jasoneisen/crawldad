using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>
/// The default in-process <see cref="IScreenshotStore"/> (§13): holds failure screenshots in memory keyed by a
/// content-addressed ref (<c>screenshots/{sha256}.png</c>), so an identical capture is stored once and the ref is a
/// credential-free hash. This is the seam's default implementation — the analogue of the in-memory download sink — with a
/// real deletable blob store slotting in behind <see cref="IScreenshotStore"/> unchanged. It tracks its blobs so a test can
/// assert a screenshot was captured on failure and that no stored key/byte carries a credential (§12 leak invariant).
/// </summary>
internal sealed class InMemoryScreenshotStore : IScreenshotStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    /// <summary>The stored screenshots keyed by ref — a white-box hook to assert capture happened and stays credential-free.</summary>
    internal IReadOnlyDictionary<string, byte[]> Blobs => _blobs;

    public Task<string> SaveAsync(byte[] png, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(png);

        var reference = $"screenshots/{Convert.ToHexStringLower(SHA256.HashData(png))}.png";
        _blobs[reference] = png;
        return Task.FromResult(reference);
    }
}
