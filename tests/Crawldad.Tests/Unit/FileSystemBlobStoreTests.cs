using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Storage;
using Crawldad.Web.Infrastructure.Storage.FileSystem;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The durable local-filesystem adapter (CD-2, §9.3/§12/§13). It runs the shared <see cref="BlobStoreContract"/> — the same
/// matrix the Azure adapter runs against Azurite — proving content-addressed idempotency, tenant partitioning, and the
/// retention lifecycle against <b>real on-disk storage</b>, with zero external dependency. The extra cases cover the
/// filesystem-specific paths: the traversal guard on the tenant segment, the temp-file / empty-store enumeration edges, and
/// the atomic-write cleanup on a failed upload.
/// </summary>
public sealed class FileSystemBlobStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crawldad-fsbs", Guid.NewGuid().ToString("N"));

    private FileSystemBlobStore Store() =>
        new(Options.Create(new StorageOptions { FileSystem = new FileSystemStorageOptions { Root = _root } }));

    [Fact]
    public async Task Download_matrix_holds_against_real_storage()
    {
        var store = Store();
        await BlobStoreContract.AssertDownloadContractAsync(store, store);
    }

    [Fact]
    public async Task Screenshot_matrix_holds_against_real_storage()
    {
        var store = Store();
        await BlobStoreContract.AssertScreenshotContractAsync(store, store);
    }

    [Fact]
    public async Task Retention_matrix_holds_against_real_storage()
    {
        var store = Store();
        await BlobStoreContract.AssertRetentionContractAsync(store, store, store);
    }

    [Fact]
    public async Task Listing_an_unwritten_store_is_empty() =>
        (await Store().ListAsync(CancellationToken.None)).ShouldBeEmpty(); // the root dir does not exist until the first write

    [Fact]
    public async Task A_partial_upload_temp_file_is_never_enumerated()
    {
        var store = Store();
        var contentId = Guid.NewGuid();
        await store.StoreAsync("tenant-a", new StoredDownload(contentId, $"{contentId}.pdf", 2, "sha"), new MemoryStream([1, 2]), CancellationToken.None);

        // Simulate a crashed in-flight write left behind next to the real blob (the ".uploading-" marker the store uses).
        var downloadsDir = Path.Combine(_root, "tenant-a", "downloads");
        await File.WriteAllTextAsync(Path.Combine(downloadsDir, $"{Guid.NewGuid()}.uploading-abc123"), "half-written");

        var keys = (await store.ListAsync(CancellationToken.None)).Select(b => b.Key).ToList();
        keys.ShouldBe([contentId.ToString()]); // only the completed blob, never the temp
    }

    [Theory]
    [InlineData("")]           // empty
    [InlineData("..")]         // parent traversal
    [InlineData("a/b")]        // forward-slash separator
    [InlineData("a\\b")]       // back-slash separator
    [InlineData("../escape")]  // traversal via ".."
    public async Task An_unsafe_tenant_segment_is_rejected(string tenant) =>
        await Should.ThrowAsync<ArgumentException>(async () => await Store().ExistsAsync(tenant, Guid.NewGuid(), CancellationToken.None));

    [Fact]
    public async Task Delete_with_an_unsafe_key_is_rejected() =>
        await Should.ThrowAsync<ArgumentException>(async () =>
            await Store().DeleteAsync(new StoredBlob(BlobKind.Download, "tenant-a", "../escape", DateTimeOffset.UtcNow, 1), CancellationToken.None));

    [Fact]
    public void The_constructor_rejects_null_options() =>
        Should.Throw<ArgumentNullException>(() => new FileSystemBlobStore(null!));

    [Fact]
    public async Task Store_rejects_a_null_item_and_a_null_stream()
    {
        var store = Store();
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await store.StoreAsync("t", null!, new MemoryStream(), CancellationToken.None));
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await store.StoreAsync("t", new StoredDownload(Guid.NewGuid(), "x", 0, "h"), null!, CancellationToken.None));
    }

    [Fact]
    public async Task Save_rejects_null_bytes() =>
        await Should.ThrowAsync<ArgumentNullException>(async () => await Store().SaveAsync("t", null!, CancellationToken.None));

    [Fact]
    public async Task Delete_rejects_a_null_blob() =>
        await Should.ThrowAsync<ArgumentNullException>(async () => await Store().DeleteAsync(null!, CancellationToken.None));

    [Fact]
    public async Task A_write_that_throws_mid_stream_leaves_no_blob_and_no_temp()
    {
        var store = Store();
        var contentId = Guid.NewGuid();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await store.StoreAsync("tenant-a", new StoredDownload(contentId, $"{contentId}.pdf", 10, "sha"), new ThrowingStream(), CancellationToken.None));

        (await store.ExistsAsync("tenant-a", contentId, CancellationToken.None)).ShouldBeFalse(); // no committed blob
        var downloadsDir = Path.Combine(_root, "tenant-a", "downloads");
        (Directory.Exists(downloadsDir) ? Directory.GetFiles(downloadsDir) : []).ShouldBeEmpty(); // the temp was cleaned up
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // A source stream that faults on read, so StoreAsync's copy throws after the temp file is created — exercising the
    // atomic-write cleanup (the temp is deleted, no committed blob is left).
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("read fault");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("read fault");

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
