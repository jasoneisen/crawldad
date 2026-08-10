using System.Text;
using Crawldad.Web.Infrastructure.Storage;

namespace Crawldad.Tests.Support;

/// <summary>The storage-seam contract every durable blob adapter must satisfy: one matrix run against each implementation
/// (filesystem in the hermetic suite, Azure against Azurite). Asserts content-addressed idempotency, tenant partitioning,
/// and the retention enumerate/delete lifecycle, using fresh unique tenants per assertion to avoid cross-contamination.</summary>
internal static class BlobStoreContract
{
    private static string NewTenant() => "t-" + Guid.NewGuid().ToString("N");

    /// <summary>Downloads are content-addressed, idempotent, and tenant-partitioned: a stored blob is visible to its own
    /// tenant, invisible to another (even by content-id probe), and a re-store of identical content adds no second blob.</summary>
    public static async Task AssertDownloadContractAsync(IDownloadSink sink, IRetentionStore retention)
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var bytes = Encoding.UTF8.GetBytes("attachment-" + Guid.NewGuid());
        var contentId = Guid.NewGuid();
        var item = new StoredDownload(contentId, $"{contentId}.pdf", bytes.Length, "sha-metadata");

        (await sink.ExistsAsync(tenantA, contentId, CancellationToken.None)).ShouldBeFalse();

        (await sink.StoreAsync(tenantA, item, new MemoryStream(bytes), CancellationToken.None)).ShouldBeTrue();
        (await sink.ExistsAsync(tenantA, contentId, CancellationToken.None)).ShouldBeTrue();  // A sees its own blob
        (await sink.ExistsAsync(tenantB, contentId, CancellationToken.None)).ShouldBeFalse(); // B cannot probe it by content id

        // Idempotent: a re-store of the same content id leaves exactly one blob under A.
        (await sink.StoreAsync(tenantA, item, new MemoryStream(bytes), CancellationToken.None)).ShouldBeTrue();
        (await CollectAsync(retention, BlobKind.Download, tenantA)).Count.ShouldBe(1);
        (await CollectAsync(retention, BlobKind.Download, tenantB)).ShouldBeEmpty();
    }

    /// <summary>Screenshots are content-addressed (identical bytes ⇒ one blob, a tenant-independent ref), tenant-partitioned,
    /// and read back byte-for-byte from — and only from — the storing tenant's own partition.</summary>
    public static async Task AssertScreenshotContractAsync(IScreenshotStore store, IRetentionStore retention)
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var png = Encoding.UTF8.GetBytes("png-" + Guid.NewGuid());

        var reference = await store.SaveAsync(tenantA, png, CancellationToken.None);
        reference.ShouldStartWith("screenshots/"); // tenant-independent ref
        reference.ShouldEndWith(".png");

        (await store.SaveAsync(tenantA, png, CancellationToken.None)).ShouldBe(reference); // idempotent: same content ⇒ same ref

        (await CollectAsync(retention, BlobKind.Screenshot, tenantA)).Count.ShouldBe(1);
        (await CollectAsync(retention, BlobKind.Screenshot, tenantB)).ShouldBeEmpty(); // B never captured one

        // Read-back: A round-trips its own bytes; B cannot read A's blob by the (shared, content-addressed) ref; an unknown ref is null.
        (await ReadAsync(store, tenantA, reference)).ShouldBe(png);
        (await ReadAsync(store, tenantB, reference)).ShouldBeNull();                       // tenant isolation on the read path
        (await ReadAsync(store, tenantA, $"screenshots/{new string('0', 64)}.png")).ShouldBeNull(); // unknown/retention-deleted
        (await ReadAsync(store, tenantA, "screenshots/not-a-valid-digest.png")).ShouldBeNull();     // malformed ref ⇒ never a blind read
    }

    private static async Task<byte[]?> ReadAsync(IScreenshotStore store, string tenant, string reference)
    {
        await using var stream = await store.OpenReadAsync(tenant, reference, CancellationToken.None);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, CancellationToken.None);
        return buffer.ToArray();
    }

    /// <summary>The retention lifecycle: a durable store enumerates what it holds (per tenant + kind) and deletes exactly what
    /// it enumerated; a second delete of the same blob reports it already gone.</summary>
    public static async Task AssertRetentionContractAsync(IDownloadSink sink, IScreenshotStore shots, IRetentionStore retention)
    {
        var tenant = NewTenant();
        var contentId = Guid.NewGuid();
        await sink.StoreAsync(tenant, new StoredDownload(contentId, $"{contentId}.bin", 3, "sha"), new MemoryStream([1, 2, 3]), CancellationToken.None);
        await shots.SaveAsync(tenant, [9, 8, 7, 6], CancellationToken.None);

        var blobs = await CollectAsync(retention, kind: null, tenant);
        blobs.Select(b => b.Kind).OrderBy(k => k).ShouldBe([BlobKind.Download, BlobKind.Screenshot]);
        blobs.ShouldAllBe(b => b.SizeBytes > 0);

        foreach (var blob in blobs)
        {
            (await retention.DeleteAsync(blob, CancellationToken.None)).ShouldBeTrue();
        }

        (await CollectAsync(retention, kind: null, tenant)).ShouldBeEmpty();
        (await retention.DeleteAsync(blobs[0], CancellationToken.None)).ShouldBeFalse(); // already gone
    }

    private static async Task<List<StoredBlob>> CollectAsync(IRetentionStore retention, BlobKind? kind, string tenant)
    {
        var all = await retention.ListAsync(CancellationToken.None);
        return [.. all.Where(blob => string.Equals(blob.Tenant, tenant, StringComparison.Ordinal) && (kind is null || blob.Kind == kind))];
    }
}
