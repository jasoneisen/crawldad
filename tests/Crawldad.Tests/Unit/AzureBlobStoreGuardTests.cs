using Crawldad.Web.Infrastructure.Storage;
using Crawldad.Web.Infrastructure.Storage.Azure;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>The Azure adapter shares the traversal guard: every operation validates the tenant (and key) segment
/// <b>before</b> any storage I/O, so a hostile tenant id can never collapse another tenant's blob prefix.
/// Deterministic and emulator-free — the guard fails closed before Azurite/Azure is ever contacted.</summary>
public class AzureBlobStoreGuardTests
{
    private static AzureBlobStore Store() =>
        new(Options.Create(new StorageOptions
        {
            Provider = StorageOptions.AzureProvider,
            Azure = new AzureStorageOptions { ConnectionString = "UseDevelopmentStorage=true", Container = "crawldad-guard" },
        }));

    [Fact]
    public async Task Exists_rejects_a_hostile_tenant_before_any_io() =>
        await Should.ThrowAsync<ArgumentException>(async () => await Store().ExistsAsync("../victim", Guid.NewGuid(), CancellationToken.None));

    [Fact]
    public async Task Store_rejects_a_hostile_tenant_before_any_io() =>
        await Should.ThrowAsync<ArgumentException>(async () =>
            await Store().StoreAsync("a/b", new StoredDownload(Guid.NewGuid(), "x", 1, "h"), new MemoryStream([1]), CancellationToken.None));

    [Fact]
    public async Task Save_rejects_a_hostile_tenant_before_any_io() =>
        await Should.ThrowAsync<ArgumentException>(async () => await Store().SaveAsync("a\\b", [1, 2], CancellationToken.None));

    [Fact]
    public async Task Open_read_rejects_a_hostile_tenant_before_any_io() => // a valid ref, so the guard rejecting the tenant is what fails it
        await Should.ThrowAsync<ArgumentException>(async () =>
            await Store().OpenReadAsync("../victim", $"screenshots/{new string('a', 64)}.png", CancellationToken.None));

    [Fact]
    public async Task Open_read_of_a_malformed_ref_is_null_without_any_io() => // rejected before the container is ever touched
        (await Store().OpenReadAsync("tenant-a", "screenshots/not-a-digest.png", CancellationToken.None)).ShouldBeNull();

    [Fact]
    public async Task Delete_rejects_a_hostile_tenant_before_any_io() =>
        await Should.ThrowAsync<ArgumentException>(async () =>
            await Store().DeleteAsync(new StoredBlob(BlobKind.Download, "../victim", "key", DateTimeOffset.UtcNow, 1), CancellationToken.None));
}
