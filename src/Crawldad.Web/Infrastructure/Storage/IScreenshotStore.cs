namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>
/// The failure-screenshot blob sink (§13 screenshot-on-failure), following the <see cref="IDownloadSink"/> ref pattern: the
/// interpreter captures a PNG from the page on a step failure and streams the <b>bytes</b> through to a deletable blob store,
/// which returns a <b>ref</b> the <c>StepFailed</c> event stores — never the image itself (§12: screenshots can show PII, so
/// they live in deletable blob storage, optionally crypto-shredded, and the immutable trace holds only the ref). Storage is
/// content-addressed so an identical screenshot is stored once and its ref is a credential-free hash (a clean value for the
/// §12 leak invariant). The default <see cref="InMemoryScreenshotStore"/> implements this in-process; a real blob-store kind
/// slots in behind the same interface exactly as the download sinks do.
/// <para>
/// The seam is <b>tenant-scoped</b> (CD-1): the run's <paramref name="tenant"/> qualifies where the bytes physically live
/// (the tenant in the key/path structure), so one tenant's screenshots are isolated from another's and CD-2's real store
/// inherits the partitioning. The returned content-addressed ref stays tenant-independent, so the <c>StepFailed</c> event
/// and timeline are byte-identical to before.
/// </para>
/// </summary>
public interface IScreenshotStore
{
    /// <summary>Stores a captured screenshot <b>under the tenant's partition</b> and returns its blob ref.</summary>
    /// <param name="tenant">The run's tenant — the storage partition the image lands in.</param>
    /// <param name="png">The PNG bytes captured from the page.</param>
    /// <param name="ct">Cancels the store.</param>
    /// <returns>The content-addressed blob ref the <c>StepFailed</c> event records.</returns>
    Task<string> SaveAsync(string tenant, byte[] png, CancellationToken ct);
}
