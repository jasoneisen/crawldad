namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>The in-memory download sink (sink kind <c>"fake"</c>), driven directly by the interpreter — no blob
/// store, no network. Tracks calls so a test can assert idempotency: a re-download of present content must
/// short-circuit on <see cref="ExistsAsync"/> and never re-enter <see cref="StoreAsync"/>.</summary>
internal sealed class FakeDownloadSink(bool failStore = false) : IDownloadSink
{
    // Blobs are held under (tenant, contentId) so the fake proves the seam's tenant partitioning: the same content
    // id under two tenants is two distinct entries, and one tenant's Exists probe never matches another's stored blob.
    private readonly HashSet<(string Tenant, Guid ContentId)> _stored = [];

    /// <summary>How many times <see cref="ExistsAsync"/> was asked (the idempotency probe count).</summary>
    internal int ExistsCalls { get; private set; }

    /// <summary>How many times <see cref="StoreAsync"/> actually ran (must stay flat across a re-download of present content).</summary>
    internal int StoreCalls { get; private set; }

    /// <summary>The content ids currently held across all tenants (a re-store of any of these, for its tenant, must not occur).</summary>
    internal IReadOnlyCollection<Guid> Stored => [.. _stored.Select(key => key.ContentId)];

    /// <summary>The content ids held for one tenant — the tenant-isolation probe (another tenant's ids never appear here).</summary>
    /// <param name="tenant">The tenant partition to inspect.</param>
    internal IReadOnlyCollection<Guid> StoredFor(string tenant) =>
        [.. _stored.Where(key => string.Equals(key.Tenant, tenant, StringComparison.Ordinal)).Select(key => key.ContentId)];

    public Task<bool> ExistsAsync(string tenant, Guid contentId, CancellationToken ct)
    {
        ExistsCalls++;
        return Task.FromResult(_stored.Contains((tenant, contentId)));
    }

    public async Task<bool> StoreAsync(string tenant, StoredDownload item, Stream content, CancellationToken ct)
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

        _stored.Add((tenant, item.ContentId));
        return true;
    }
}
