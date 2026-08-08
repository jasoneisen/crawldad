using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Crawldad.Web.Infrastructure.Storage.FileSystem;

/// <summary>
/// The durable local-filesystem blob store (CD-2, §9.3/§12/§13): the hermetic, dependency-free implementation of all three
/// storage seams — the <see cref="IDownloadSink"/> (content-addressed, idempotent), the <see cref="IScreenshotStore"/>
/// (content-addressed failure screenshots), and the <see cref="IRetentionStore"/> the janitor sweeps. One instance backs all
/// three so the tenant-partitioning and pathing live in one place.
/// <para>
/// <b>Layout (tenant-partitioned, CD-1/§12):</b> <c>{Root}/{tenant}/downloads/{contentId}</c> and
/// <c>{Root}/{tenant}/screenshots/{sha256}.png</c>. The tenant is the first path segment, so one tenant can neither read,
/// overwrite, nor probe another's blob — the idempotency probe (<see cref="ExistsAsync"/>) resolves a tenant-qualified path.
/// The engine-facing content id / screenshot ref stay tenant-independent (the wire result and immutable trace are
/// byte-identical); the tenant only qualifies where the bytes physically live.
/// </para>
/// <para>
/// <b>Durability:</b> writes go to a temp file and are atomically moved into place, so <see cref="ExistsAsync"/> never sees a
/// half-written blob (content addressing would otherwise be broken by a torn write). <b>Traversal-safe:</b> the only
/// attacker-influenced input is the tenant string (from the authenticated principal, config-validated) — it is still guarded
/// against path traversal; the content id is a GUID and the screenshot key is a hex digest, both intrinsically safe.
/// </para>
/// </summary>
internal sealed class FileSystemBlobStore : IDownloadSink, IScreenshotStore, IRetentionStore
{
    private const string _downloadsDir = "downloads";
    private const string _screenshotsDir = "screenshots";

    // In-flight writes land under this marker then move into place; the marker also excludes a leftover (crashed) temp from
    // enumeration so it can never masquerade as a complete blob.
    private const string _tempMarker = ".uploading-";

    private static readonly char[] _separators = ['/', '\\'];
    private static readonly BlobKind[] _kinds = [BlobKind.Download, BlobKind.Screenshot];

    private readonly string _root;

    /// <summary>Binds the store to its configured root directory (created lazily on first write).</summary>
    /// <param name="options">The storage options carrying <c>FileSystem:Root</c>.</param>
    public FileSystemBlobStore(IOptions<StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = options.Value.FileSystem.Root;
    }

    // ----- IDownloadSink (content-addressed, idempotent, tenant-partitioned) --------------------------------------------

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string tenant, Guid contentId, CancellationToken ct) =>
        Task.FromResult(File.Exists(DownloadPath(tenant, contentId)));

    /// <inheritdoc/>
    public async Task<bool> StoreAsync(string tenant, StoredDownload item, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(content);

        await WriteAtomicallyAsync(DownloadPath(tenant, item.ContentId), content, ct);
        return true;
    }

    // ----- IScreenshotStore (content-addressed, tenant-partitioned) -----------------------------------------------------

    /// <inheritdoc/>
    public async Task<string> SaveAsync(string tenant, byte[] png, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(png);

        var digest = Convert.ToHexStringLower(SHA256.HashData(png));
        var reference = $"{_screenshotsDir}/{digest}.png"; // tenant-independent — the StepFailed event/timeline stay byte-identical
        var path = ScreenshotPath(tenant, digest);

        if (!File.Exists(path)) // content-addressed ⇒ identical bytes collapse to one blob (idempotent capture)
        {
            await using var buffer = new MemoryStream(png, writable: false);
            await WriteAtomicallyAsync(path, buffer, ct);
        }

        return reference;
    }

    // ----- IRetentionStore (the janitor sweep + PII erasure primitive) --------------------------------------------------

    /// <inheritdoc/>
    public Task<IReadOnlyList<StoredBlob>> ListAsync(CancellationToken ct)
    {
        var blobs = new List<StoredBlob>();
        if (Directory.Exists(_root)) // nothing written yet ⇒ empty
        {
            foreach (var tenantDir in Directory.EnumerateDirectories(_root))
            {
                var tenant = Path.GetFileName(tenantDir);
                foreach (var kind in _kinds)
                {
                    var dir = Path.Combine(tenantDir, SubDir(kind));
                    if (!Directory.Exists(dir))
                    {
                        continue; // this tenant has no blobs of this kind
                    }

                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        if (IsTemp(file))
                        {
                            continue; // a crashed in-flight write — never a complete blob
                        }

                        var info = new FileInfo(file);
                        blobs.Add(new StoredBlob(kind, tenant, info.Name, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length));
                    }
                }
            }
        }

        return Task.FromResult<IReadOnlyList<StoredBlob>>(blobs);
    }

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(StoredBlob blob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(blob);

        var path = Path.Combine(_root, SafeSegment(blob.Tenant), SubDir(blob.Kind), SafeSegment(blob.Key));
        if (!File.Exists(path))
        {
            return Task.FromResult(false); // already gone (a concurrent sweep / erasure)
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    // ----- pathing + guards ---------------------------------------------------------------------------------------------

    private string DownloadPath(string tenant, Guid contentId) =>
        Path.Combine(_root, SafeSegment(tenant), _downloadsDir, contentId.ToString());

    private string ScreenshotPath(string tenant, string digest) =>
        Path.Combine(_root, SafeSegment(tenant), _screenshotsDir, $"{digest}.png");

    private static string SubDir(BlobKind kind) => kind == BlobKind.Download ? _downloadsDir : _screenshotsDir;

    private static bool IsTemp(string path) => path.Contains(_tempMarker, StringComparison.Ordinal);

    // Guards the one attacker-influenceable path segment (the tenant) against traversal: no separators, no "..", non-empty.
    // The content id (a GUID) and screenshot key (a hex digest) are intrinsically safe and pass through unchanged.
    private static string SafeSegment(string value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Contains("..", StringComparison.Ordinal)
            || value.IndexOfAny(_separators) >= 0)
        {
            throw new ArgumentException($"'{value}' is not a safe storage path segment", nameof(value));
        }

        return value;
    }

    // Writes to a temp sibling then atomically moves it into place, so a reader (ExistsAsync) never sees a torn write and a
    // re-store of identical content is a harmless overwrite (same content id ⇒ same bytes). A failed write leaves no temp.
    private static async Task WriteAtomicallyAsync(string path, Stream content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + _tempMarker + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = File.Create(temp))
            {
                await content.CopyToAsync(file, ct);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp); // a write that threw before the move — clean the partial temp
            }
        }
    }
}
