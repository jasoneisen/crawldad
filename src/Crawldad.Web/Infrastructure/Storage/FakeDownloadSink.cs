namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>
/// The in-memory download sink (§9.3 testing seam), registered under the sink kind <c>"fake"</c> and driven directly by
/// the interpreter — no blob store, no network. It tracks its calls so a test can assert the content-addressed
/// idempotency contract by construction: a re-download of already-present content must short-circuit on
/// <see cref="ExistsAsync"/> and <b>never</b> re-enter <see cref="StoreAsync"/> (assert <see cref="StoreCalls"/> stays
/// put). Construct it with <paramref name="failStore"/> to model a sink that rejects the handling (the reference's
/// <c>handleDownload</c> returning <c>false</c>), driving <c>dl.stored == false</c> and the payload's warn branch.
/// </summary>
/// <param name="failStore">When true, <see cref="StoreAsync"/> drains the stream but returns <see langword="false"/> and stores nothing.</param>
internal sealed class FakeDownloadSink(bool failStore = false) : IDownloadSink
{
    private readonly HashSet<Guid> _stored = [];

    /// <summary>How many times <see cref="ExistsAsync"/> was asked (the idempotency probe count).</summary>
    internal int ExistsCalls { get; private set; }

    /// <summary>How many times <see cref="StoreAsync"/> actually ran (must stay flat across a re-download of present content).</summary>
    internal int StoreCalls { get; private set; }

    /// <summary>The content ids currently held (a re-store of any of these must not occur).</summary>
    internal IReadOnlyCollection<Guid> Stored => _stored;

    public Task<bool> ExistsAsync(Guid contentId, CancellationToken ct)
    {
        ExistsCalls++;
        return Task.FromResult(_stored.Contains(contentId));
    }

    public async Task<bool> StoreAsync(StoredDownload item, Stream content, CancellationToken ct)
    {
        StoreCalls++;

        // Drain the stream so size is observed exactly as a real upload would consume it (and so a caller cannot pass a
        // still-open reader off as "stored").
        using var drain = new MemoryStream();
        await content.CopyToAsync(drain, ct);

        if (failStore)
        {
            return false; // scripted handling failure → dl.stored == false
        }

        _stored.Add(item.ContentId);
        return true;
    }
}
