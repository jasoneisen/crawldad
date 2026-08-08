using System.Threading;
using Crawldad.Web.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The retention janitor (CD-2, §12/§13): <see cref="RetentionJanitor.SweepOnceAsync"/> deletes blobs past their category's
/// TTL (a ≤ 0 TTL retains that category), counting deletions; the periodic driver sweeps repeatedly and stops cleanly on host
/// shutdown, and is a no-op when retention is disabled.
/// </summary>
public class RetentionJanitorTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static RetentionJanitor Janitor(RetentionOptions retention, params IRetentionStore[] stores) =>
        new(stores, Options.Create(new StorageOptions { Retention = retention }), TimeProvider.System, NullLogger<RetentionJanitor>.Instance);

    [Fact]
    public async Task Sweep_deletes_expired_keeps_fresh_and_skips_a_disabled_category()
    {
        var store = new FakeRetentionStore();
        var expired = new StoredBlob(BlobKind.Download, "t", "old", _now.AddHours(-2), 10);       // past the 1h TTL → delete
        var fresh = new StoredBlob(BlobKind.Download, "t", "new", _now.AddMinutes(-10), 10);      // within the 1h TTL → keep
        var shot = new StoredBlob(BlobKind.Screenshot, "t", "shot", _now.AddDays(-100), 10);      // screenshot TTL disabled → keep
        store.Blobs.AddRange([expired, fresh, shot]);

        var janitor = Janitor(new RetentionOptions { DownloadTtl = TimeSpan.FromHours(1), ScreenshotTtl = TimeSpan.Zero }, store);

        var deleted = await janitor.SweepOnceAsync(_now, CancellationToken.None);

        deleted.ShouldBe(1);
        store.Deleted.ShouldBe([expired]); // only the aged-out download
    }

    [Fact]
    public async Task A_blob_that_is_already_gone_is_not_counted()
    {
        var store = new FakeRetentionStore { DeleteResult = false };
        store.Blobs.Add(new StoredBlob(BlobKind.Download, "t", "old", _now.AddDays(-40), 10));

        var deleted = await Janitor(new RetentionOptions { DownloadTtl = TimeSpan.FromDays(30) }, store).SweepOnceAsync(_now, CancellationToken.None);

        deleted.ShouldBe(0); // DeleteAsync reported it already gone → not counted, nothing logged
    }

    [Fact]
    public async Task A_failing_blob_delete_is_logged_and_the_sweep_continues()
    {
        var store = new FakeRetentionStore { DeleteFault = new InvalidOperationException("azure 503") };
        store.Blobs.Add(new StoredBlob(BlobKind.Download, "t", "old", _now.AddDays(-40), 10)); // expired ⇒ delete attempted

        // The delete throws, but the sweep swallows it and reports zero deletions rather than crashing.
        var deleted = await Janitor(new RetentionOptions { DownloadTtl = TimeSpan.FromDays(30) }, store).SweepOnceAsync(_now, CancellationToken.None);

        deleted.ShouldBe(0);
        store.Deleted.Count.ShouldBe(1); // the delete was attempted
    }

    [Fact]
    public async Task Cancellation_during_a_delete_is_not_swallowed()
    {
        var store = new FakeRetentionStore { DeleteFault = new OperationCanceledException() };
        store.Blobs.Add(new StoredBlob(BlobKind.Download, "t", "old", _now.AddDays(-40), 10));

        // Cancellation is not a transient error — it propagates so shutdown stops the sweep promptly.
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Janitor(new RetentionOptions { DownloadTtl = TimeSpan.FromDays(30) }, store).SweepOnceAsync(_now, CancellationToken.None));
    }

    [Fact]
    public async Task A_safe_sweep_absorbs_a_store_failure_but_reraises_cancellation()
    {
        // A store-level failure (e.g. ListAsync throwing) is absorbed — the host survives, the sweep retries next interval.
        var failing = Janitor(new RetentionOptions(), new FakeRetentionStore { ListFault = new InvalidOperationException("azure 500") });
        await Should.NotThrowAsync(async () => await failing.SweepSafelyAsync(CancellationToken.None));

        // …but a cancellation still propagates, so the periodic loop tears down on shutdown.
        var cancelling = Janitor(new RetentionOptions(), new FakeRetentionStore { ListFault = new OperationCanceledException() });
        await Should.ThrowAsync<OperationCanceledException>(async () => await cancelling.SweepSafelyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_store_that_keeps_failing_does_not_stop_the_host()
    {
        // The periodic driver keeps ticking through repeated sweep failures instead of letting BackgroundService StopHost fire.
        var store = new FakeRetentionStore { ListFault = new InvalidOperationException("azure 500") };
        var janitor = Janitor(new RetentionOptions { Enabled = true, SweepInterval = TimeSpan.FromMilliseconds(20) }, store);

        await janitor.StartAsync(CancellationToken.None);
        await Task.WhenAny(store.SweptTwice.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await janitor.StopAsync(CancellationToken.None);

        store.SweptTwice.Task.IsCompletedSuccessfully.ShouldBeTrue(); // it kept sweeping despite every sweep failing
    }

    [Fact]
    public async Task Sweeping_with_no_stores_or_an_empty_store_deletes_nothing()
    {
        (await Janitor(new RetentionOptions()).SweepOnceAsync(_now, CancellationToken.None)).ShouldBe(0);
        (await Janitor(new RetentionOptions(), new FakeRetentionStore()).SweepOnceAsync(_now, CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task Disabled_retention_never_sweeps()
    {
        var store = new FakeRetentionStore();
        var janitor = Janitor(new RetentionOptions { Enabled = false, SweepInterval = TimeSpan.FromMilliseconds(5) }, store);

        await janitor.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await janitor.StopAsync(CancellationToken.None);

        store.Enumerations.ShouldBe(0); // ExecuteAsync returned immediately
    }

    [Fact]
    public async Task The_periodic_driver_sweeps_repeatedly_and_stops_cleanly()
    {
        var store = new FakeRetentionStore(); // empty — the loop just needs to tick
        var janitor = Janitor(new RetentionOptions { Enabled = true, SweepInterval = TimeSpan.FromMilliseconds(20) }, store);

        await janitor.StartAsync(CancellationToken.None);
        await Task.WhenAny(store.SweptTwice.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await janitor.StopAsync(CancellationToken.None); // cancels the loop mid-wait — it exits without throwing

        store.SweptTwice.Task.IsCompletedSuccessfully.ShouldBeTrue();
        store.Enumerations.ShouldBeGreaterThanOrEqualTo(2);
    }

    // A controllable retention store: yields a scripted blob set, records deletes, and signals after two sweeps so the loop
    // test is deterministic. Empty Blobs (the loop test) means enumeration never hits a mid-sweep cancellation point.
    private sealed class FakeRetentionStore : IRetentionStore
    {
        private int _enumerations;

        public List<StoredBlob> Blobs { get; } = [];

        public List<StoredBlob> Deleted { get; } = [];

        public bool DeleteResult { get; init; } = true;

        /// <summary>When set, <see cref="ListAsync"/> faults with it — a transient store failure the sweep must survive.</summary>
        public Exception? ListFault { get; init; }

        /// <summary>When set, <see cref="DeleteAsync"/> faults with it — a per-blob failure the sweep must log and skip.</summary>
        public Exception? DeleteFault { get; init; }

        public int Enumerations => Volatile.Read(ref _enumerations);

        public TaskCompletionSource SweptTwice { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<StoredBlob>> ListAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _enumerations) >= 2)
            {
                SweptTwice.TrySetResult();
            }

            return ListFault is not null
                ? Task.FromException<IReadOnlyList<StoredBlob>>(ListFault)
                : Task.FromResult<IReadOnlyList<StoredBlob>>(Blobs);
        }

        public Task<bool> DeleteAsync(StoredBlob blob, CancellationToken ct)
        {
            Deleted.Add(blob);
            return DeleteFault is not null ? Task.FromException<bool>(DeleteFault) : Task.FromResult(DeleteResult);
        }
    }
}
