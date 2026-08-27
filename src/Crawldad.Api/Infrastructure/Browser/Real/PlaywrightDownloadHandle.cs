using Microsoft.Playwright;

namespace Crawldad.Api.Infrastructure.Browser.Real;

/// <summary>A thin wrapper over a Playwright <see cref="IDownload"/>.</summary>
internal sealed class PlaywrightDownloadHandle(IDownload download) : IDownloadHandle
{
    public string SuggestedFilename => download.SuggestedFilename;

    public Task<Stream> OpenReadAsync(CancellationToken ct) => download.CreateReadStreamAsync();
}
