namespace Crawldad.Web.Infrastructure.Browser.Fake;

/// <summary>
/// A record/replay download (§ Deliverable 2): serves fixture bytes in memory. <see cref="OpenReadAsync"/> hands back a
/// fresh non-writable stream each call, so the engine can drain it (to hash + stream to the sink) independently of any
/// other reader — mirroring a real adapter's re-openable temp file, with no temp-file lifecycle to manage here.
/// </summary>
/// <param name="bytes">The downloaded body.</param>
/// <param name="suggestedFilename">The download's HTTP-suggested filename.</param>
internal sealed class FakeDownloadHandle(byte[] bytes, string suggestedFilename) : IDownloadHandle
{
    public string SuggestedFilename => suggestedFilename;

    public Task<Stream> OpenReadAsync(CancellationToken ct) =>
        Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
}
