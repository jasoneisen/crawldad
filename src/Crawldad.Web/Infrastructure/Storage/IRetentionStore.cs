namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>The category of a stored blob, selecting which retention TTL applies (§12/§13).</summary>
public enum BlobKind
{
    /// <summary>A downloaded attachment (§9.3) — the caller's bulk data.</summary>
    Download,

    /// <summary>A failure screenshot (§13) — can show PII, so it gets the shorter retention.</summary>
    Screenshot,
}

/// <summary>
/// One durable blob a retention sweep can age out (§12/§13). <see cref="Kind"/> selects the TTL, <see cref="Tenant"/>
/// and <see cref="Key"/> identify the physical blob (its partition + leaf name), and <see cref="LastModifiedUtc"/> is
/// the age basis. Handed straight back to <see cref="IRetentionStore.DeleteAsync"/> so the store deletes exactly what
/// it enumerated — the janitor never reconstructs a path.
/// </summary>
/// <param name="Kind">The blob category (download vs screenshot) — selects the retention TTL.</param>
/// <param name="Tenant">The tenant partition the blob lives under (CD-1).</param>
/// <param name="Key">The blob's leaf name within its tenant/kind partition.</param>
/// <param name="LastModifiedUtc">When the blob was last written — the age basis for the TTL check.</param>
/// <param name="SizeBytes">The blob's size in bytes (observability).</param>
public sealed record StoredBlob(BlobKind Kind, string Tenant, string Key, DateTimeOffset LastModifiedUtc, long SizeBytes);

/// <summary>
/// The lifecycle seam a <b>durable</b> blob store exposes so the host can enforce the §12/§13 retention policies and honour
/// PII erasure (deletable blobs). The scheduled <see cref="RetentionJanitor"/> enumerates every stored blob across every
/// tenant and deletes the ones past their category's TTL; the same <see cref="DeleteAsync"/> primitive backs an on-demand
/// erasure. Only durable adapters (filesystem, Azure Blob) implement this — the in-memory fakes are ephemeral, so they need
/// no sweeper and register none, and the janitor is then a harmless no-op. Enumeration spans all tenants because retention is
/// a host-wide policy; the returned <see cref="StoredBlob"/> carries the tenant so per-tenant partitioning (CD-1) is preserved
/// end to end.
/// </summary>
public interface IRetentionStore
{
    /// <summary>Lists every durable blob across all tenants and categories — the retention sweep's input. Materialized (the
    /// codebase's <c>ToListAsync</c> convention) rather than streamed; a sweep's blob set is bounded and this is metadata only.</summary>
    /// <param name="ct">Cancels the listing.</param>
    /// <returns>Each stored blob with its category, tenant, key, last-modified time, and size.</returns>
    Task<IReadOnlyList<StoredBlob>> ListAsync(CancellationToken ct);

    /// <summary>Deletes the blob the sweep enumerated (retention or PII erasure).</summary>
    /// <param name="blob">The blob to delete (as returned by <see cref="EnumerateAsync"/>).</param>
    /// <param name="ct">Cancels the delete.</param>
    /// <returns><see langword="true"/> when a blob was deleted; <see langword="false"/> when it was already gone.</returns>
    Task<bool> DeleteAsync(StoredBlob blob, CancellationToken ct);
}
