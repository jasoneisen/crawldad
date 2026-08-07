namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>
/// The failure-screenshot blob sink (§13 screenshot-on-failure), following the <see cref="IDownloadSink"/> ref pattern: the
/// interpreter captures a PNG from the page on a step failure and streams the <b>bytes</b> through to a deletable blob store,
/// which returns a <b>ref</b> the <c>StepFailed</c> event stores — never the image itself (§12: screenshots can show PII, so
/// they live in deletable blob storage, optionally crypto-shredded, and the immutable trace holds only the ref). Storage is
/// content-addressed so an identical screenshot is stored once and its ref is a credential-free hash (a clean value for the
/// §12 leak invariant). The default <see cref="InMemoryScreenshotStore"/> implements this in-process; a real blob-store kind
/// slots in behind the same interface exactly as the download sinks do.
/// </summary>
public interface IScreenshotStore
{
    /// <summary>Stores a captured screenshot and returns its blob ref.</summary>
    /// <param name="png">The PNG bytes captured from the page.</param>
    /// <param name="ct">Cancels the store.</param>
    /// <returns>The content-addressed blob ref the <c>StepFailed</c> event records.</returns>
    Task<string> SaveAsync(byte[] png, CancellationToken ct);
}
