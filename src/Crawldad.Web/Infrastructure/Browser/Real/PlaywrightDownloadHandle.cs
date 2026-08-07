using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>
/// A thin wrapper over a Playwright <see cref="IDownload"/> (§9.3). The engine reads the bytes to compute the content
/// identity and streams them to the sink; <see cref="OpenReadAsync"/> maps to <c>download.CreateReadStreamAsync</c>,
/// which serves the completed download's bytes without the caller managing the temp-file lifecycle by hand.
/// </summary>
/// <param name="download">The wrapped Playwright download.</param>
internal sealed class PlaywrightDownloadHandle(IDownload download) : IDownloadHandle
{
    public string SuggestedFilename => download.SuggestedFilename;

    public Task<Stream> OpenReadAsync(CancellationToken ct) => download.CreateReadStreamAsync();
}
