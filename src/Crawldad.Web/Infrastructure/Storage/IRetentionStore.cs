namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>The category of a stored blob, selecting which retention TTL applies.</summary>
public enum BlobKind
{
    /// <summary>A downloaded attachment — the caller's bulk data.</summary>
    Download,

    /// <summary>A failure screenshot — can show PII, so it gets the shorter retention.</summary>
    Screenshot,
}

/// <summary>One durable blob a retention sweep can age out. <see cref="Kind"/> selects the TTL; handed straight back
/// to <see cref="IRetentionStore.DeleteAsync"/> so the store deletes exactly what it enumerated — the janitor never
/// reconstructs a path.</summary>
public sealed record StoredBlob(BlobKind Kind, string Tenant, string Key, DateTimeOffset LastModifiedUtc, long SizeBytes);

/// <summary>The lifecycle seam a durable blob store exposes so the host can enforce retention policy and honour PII
/// erasure (deletable blobs). The scheduled <see cref="RetentionJanitor"/> enumerates every stored blob and deletes
/// those past their category's TTL; in-memory fakes implement nothing here, so the janitor is a harmless no-op.</summary>
public interface IRetentionStore
{
    /// <summary>Lists every durable blob across all tenants and categories — the retention sweep's input.</summary>
    Task<IReadOnlyList<StoredBlob>> ListAsync(CancellationToken ct);

    /// <summary>Deletes the blob the sweep listed (retention or PII erasure); <see langword="true"/> when a blob was
    /// deleted, <see langword="false"/> when it was already gone.</summary>
    Task<bool> DeleteAsync(StoredBlob blob, CancellationToken ct);
}
