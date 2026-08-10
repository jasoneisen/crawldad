namespace Crawldad.Web.Infrastructure.Browser.Fake;

/// <summary>A record/replay download that serves fixture bytes in memory. <see cref="OpenReadAsync"/> hands back a
/// fresh non-writable stream each call, so readers can drain independently — mirroring a real adapter's re-openable
/// temp file, with no temp-file lifecycle to manage here.</summary>
internal sealed class FakeDownloadHandle(byte[] bytes, string suggestedFilename) : IDownloadHandle
{
    public string SuggestedFilename => suggestedFilename;

    public Task<Stream> OpenReadAsync(CancellationToken ct) =>
        Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
}
