namespace Crawldad.Api.Infrastructure.Storage;

/// <summary>Metadata the engine hands a sink alongside the byte stream. The sink stores content under its
/// content-addressed identity; the extra fields let a real sink record size/hash without re-reading the stream.</summary>
public sealed record StoredDownload(Guid ContentId, string StoredAs, long SizeBytes, string Sha256);

/// <summary>The byte-sink seam: the engine streams downloaded bytes straight through to a sink so they never buffer
/// into an event, aggregate, or response. Storage is content-addressed and idempotent — <see cref="ExistsAsync"/>
/// short-circuits an already-present blob to no re-upload — and tenant-scoped, so one tenant can't reach another's blobs.</summary>
public interface IDownloadSink
{
    /// <summary>Whether a blob with this content id is already stored for this tenant (the idempotency short-circuit).</summary>
    Task<bool> ExistsAsync(string tenant, Guid contentId, CancellationToken ct);

    /// <summary>Stores the content under the tenant's partition, returning whether the handling succeeded — bound
    /// into <c>dl.stored</c> (store ⇒ keep the attachment; failure ⇒ warn and drop it).</summary>
    Task<bool> StoreAsync(string tenant, StoredDownload item, Stream content, CancellationToken ct);
}
