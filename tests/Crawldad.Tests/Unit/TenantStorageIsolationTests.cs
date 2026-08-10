using Crawldad.Web.Infrastructure.Storage;

namespace Crawldad.Tests.Unit;

/// <summary>The storage seams are tenant-scoped: the download-sink and screenshot-store fakes partition by tenant so a real
/// adapter inherits the isolation. One tenant can neither read, overwrite, nor probe another's blob — proven directly
/// here on the fakes, complementing the end-to-end cross-tenant test.</summary>
public class TenantStorageIsolationTests
{
    [Fact]
    public async Task Download_sink_partitions_content_by_tenant()
    {
        var sink = new FakeDownloadSink();
        var contentId = Guid.NewGuid();
        var item = new StoredDownload(contentId, $"{contentId}.pdf", 3, "hash");

        (await sink.StoreAsync("tenant-a", item, new MemoryStream([1, 2, 3]), CancellationToken.None)).ShouldBeTrue();

        (await sink.ExistsAsync("tenant-a", contentId, CancellationToken.None)).ShouldBeTrue();  // A sees its own blob…
        (await sink.ExistsAsync("tenant-b", contentId, CancellationToken.None)).ShouldBeFalse(); // …B cannot probe it by content id
        sink.StoredFor("tenant-a").ShouldContain(contentId);
        sink.StoredFor("tenant-b").ShouldBeEmpty();
    }

    [Fact]
    public async Task Screenshot_store_partitions_captures_by_tenant()
    {
        var store = new InMemoryScreenshotStore();

        var reference = await store.SaveAsync("tenant-a", [1, 2, 3], CancellationToken.None);

        reference.ShouldStartWith("screenshots/"); // the content-addressed ref stays tenant-independent (wire/trace unchanged)
        store.ReferencesFor("tenant-a").ShouldContain(reference);
        store.ReferencesFor("tenant-b").ShouldBeEmpty();

        // …and the read path honours the same partition: B cannot read A's capture even by the shared, content-addressed ref.
        (await store.OpenReadAsync("tenant-a", reference, CancellationToken.None)).ShouldNotBeNull();
        (await store.OpenReadAsync("tenant-b", reference, CancellationToken.None)).ShouldBeNull();
    }
}
