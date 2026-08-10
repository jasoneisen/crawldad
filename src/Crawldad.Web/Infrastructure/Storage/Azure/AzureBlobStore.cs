using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace Crawldad.Web.Infrastructure.Storage.Azure;

/// <summary>The durable Azure Blob storage adapter: implements all three storage seams over one container,
/// partitioning by a <c>{tenant}/{downloads|screenshots}/…</c> blob-name prefix so one tenant can neither read,
/// overwrite, nor probe another's blob. Uploads are content-addressed and idempotent; the container is created lazily.</summary>
[ExcludeFromCodeCoverage(Justification =
    "Exercised against the Azurite emulator (opt-in AzuriteBlobStoreTests + the CI Azurite service), never live storage. " +
    "Excluded from the hermetic 100% gate, which runs with zero external storage dependencies; the FileSystemBlobStore " +
    "carries the seam's covered implementation. Construction is I/O-free, so StorageModule's azure wiring is covered without Azurite.")]
internal sealed class AzureBlobStore : IDownloadSink, IScreenshotStore, IRetentionStore
{
    private readonly BlobContainerClient _container;
    private Task? _containerReady;

    /// <summary>Binds the store to its configured container (created lazily; the constructor performs no I/O).</summary>
    /// <param name="options">The storage options carrying <c>Azure:ConnectionString</c> and <c>Azure:Container</c>.</param>
    public AzureBlobStore(IOptions<StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var azure = options.Value.Azure;
        _container = new BlobServiceClient(azure.ConnectionString).GetBlobContainerClient(azure.Container);
    }

    // ----- IDownloadSink ------------------------------------------------------------------------------------------------

    public async Task<bool> ExistsAsync(string tenant, Guid contentId, CancellationToken ct)
    {
        var name = DownloadName(tenant, contentId); // validates the tenant (traversal guard) before any I/O
        await EnsureContainerAsync(ct);
        return await _container.GetBlobClient(name).ExistsAsync(ct);
    }

    public async Task<bool> StoreAsync(string tenant, StoredDownload item, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(content);

        var name = DownloadName(tenant, item.ContentId); // validates the tenant (traversal guard) before any I/O
        await EnsureContainerAsync(ct);
        await _container.GetBlobClient(name).UploadAsync(content, overwrite: true, ct);
        return true;
    }

    // ----- IScreenshotStore ---------------------------------------------------------------------------------------------

    public async Task<string> SaveAsync(string tenant, byte[] png, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(png);

        var digest = Convert.ToHexStringLower(SHA256.HashData(png));
        var reference = $"{BlobNaming.ScreenshotsDir}/{digest}.png"; // tenant-independent — the trace stays byte-identical
        var name = ScreenshotName(tenant, digest); // validates the tenant (traversal guard) before any I/O

        await EnsureContainerAsync(ct);
        var blob = _container.GetBlobClient(name);
        if (!await blob.ExistsAsync(ct))
        {
            await using var buffer = new MemoryStream(png, writable: false);
            await blob.UploadAsync(buffer, overwrite: true, ct);
        }

        return reference;
    }

    // ----- IRetentionStore ----------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<StoredBlob>> ListAsync(CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        var blobs = new List<StoredBlob>();
        await foreach (var item in _container.GetBlobsAsync(traits: BlobTraits.None, states: BlobStates.None, prefix: null, cancellationToken: ct))
        {
            if (TryParseName(item.Name, out var kind, out var tenant, out var key))
            {
                blobs.Add(new StoredBlob(
                    kind,
                    tenant,
                    key,
                    item.Properties.LastModified ?? DateTimeOffset.MinValue,
                    item.Properties.ContentLength ?? 0));
            }
        }

        return blobs;
    }

    public async Task<bool> DeleteAsync(StoredBlob blob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(blob);

        // Validate both attacker-influenceable segments (traversal guard) before any I/O.
        var name = $"{BlobNaming.SafeSegment(blob.Tenant)}/{BlobNaming.SubDir(blob.Kind)}/{BlobNaming.SafeSegment(blob.Key)}";
        await EnsureContainerAsync(ct);
        return await _container.GetBlobClient(name).DeleteIfExistsAsync(cancellationToken: ct);
    }

    // ----- naming + lazy init -------------------------------------------------------------------------------------------

    private static string DownloadName(string tenant, Guid contentId) => $"{BlobNaming.SafeSegment(tenant)}/{BlobNaming.DownloadsDir}/{contentId}";

    private static string ScreenshotName(string tenant, string digest) => $"{BlobNaming.SafeSegment(tenant)}/{BlobNaming.ScreenshotsDir}/{digest}.png";

    // Parses "{tenant}/{downloads|screenshots}/{key}"; a blob that doesn't match the layout (foreign content) is skipped.
    private static bool TryParseName(string name, out BlobKind kind, out string tenant, out string key)
    {
        kind = default;
        tenant = "";
        key = "";

        var parts = name.Split('/', 3);
        if (parts.Length != 3)
        {
            return false;
        }

        kind = parts[1] switch
        {
            BlobNaming.DownloadsDir => BlobKind.Download,
            BlobNaming.ScreenshotsDir => BlobKind.Screenshot,
            _ => (BlobKind)(-1),
        };
        if ((int)kind < 0)
        {
            return false;
        }

        tenant = parts[0];
        key = parts[2];
        return true;
    }

    // Lazily create the container once. A rare race just starts two idempotent CreateIfNotExists calls (harmless); the shared
    // Task is then awaited by all callers. No disposable state, so the singleton needs no teardown.
    private Task EnsureContainerAsync(CancellationToken ct) =>
        _containerReady ??= _container.CreateIfNotExistsAsync(cancellationToken: ct);
}
