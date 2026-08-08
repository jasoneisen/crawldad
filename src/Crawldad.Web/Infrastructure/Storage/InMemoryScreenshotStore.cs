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
    // Physical key is "{tenant}/{ref}" so the fake proves the seam's tenant partitioning (CD-1): the same screenshot bytes
    // under two tenants are two distinct blobs, and one tenant's capture is invisible under another's partition.
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    /// <summary>The stored screenshots keyed by their content-addressed ref (flattened across tenants) — a white-box hook
    /// to assert capture happened and stays credential-free. The ref is content-addressed, so identical bytes collapse to
    /// one entry regardless of tenant; use <see cref="ReferencesFor"/> to assert per-tenant isolation.</summary>
    internal IReadOnlyDictionary<string, byte[]> Blobs =>
        _blobs.ToDictionary(entry => entry.Key[(entry.Key.IndexOf('/', StringComparison.Ordinal) + 1)..], entry => entry.Value, StringComparer.Ordinal);

    /// <summary>The refs captured under one tenant's partition — the tenant-isolation probe (another tenant's captures never appear here).</summary>
    /// <param name="tenant">The tenant partition to inspect.</param>
    internal IReadOnlyCollection<string> ReferencesFor(string tenant)
    {
        var prefix = tenant + "/";
        return [.. _blobs.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).Select(key => key[prefix.Length..])];
    }

    public Task<string> SaveAsync(string tenant, byte[] png, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(png);

        var reference = $"screenshots/{Convert.ToHexStringLower(SHA256.HashData(png))}.png";
        _blobs[$"{tenant}/{reference}"] = png;
        return Task.FromResult(reference);
    }
}
