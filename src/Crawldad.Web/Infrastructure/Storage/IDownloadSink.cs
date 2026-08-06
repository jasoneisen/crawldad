namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>
/// Metadata the engine hands a sink alongside the byte stream (§9.3). The sink stores the content under its
/// content-addressed identity; the extra fields let a real Phase 4 sink record size/hash without re-reading the stream.
/// </summary>
/// <param name="ContentId">The content-addressed id (first 16 bytes of the SHA-256 as a GUID) — the idempotency key.</param>
/// <param name="StoredAs">The engine's stored-blob name (<c>{contentId}.{ext}</c>, from the suggested filename).</param>
/// <param name="SizeBytes">The downloaded byte count.</param>
/// <param name="Sha256">The full SHA-256 of the bytes, lowercase hex.</param>
public sealed record StoredDownload(Guid ContentId, string StoredAs, long SizeBytes, string Sha256);

/// <summary>
/// The byte-sink seam (§9.3/§9.4, the <c>IEmailGateway</c> idiom for downloads). The engine streams downloaded bytes
/// straight through to a sink — a caller presigned URL or a Crawldad blob store — so bytes never buffer into an event,
/// aggregate, or response (§14). Storage is <b>content-addressed and idempotent</b>: the engine computes the SHA-256
/// while draining the stream, derives the <c>contentId</c>, and asks <see cref="ExistsAsync"/> first — an
/// already-present blob short-circuits to <c>stored:true</c> with <b>no</b> re-upload (reproducing
/// <c>handleDownload</c>'s blob-exists check). A <c>FakeDownloadSink</c> implements this in-memory for tests and the
/// fake-adapter path; Phase 4 adds presigned-URL / blob-store kinds behind the same interface.
/// </summary>
public interface IDownloadSink
{
    /// <summary>Whether a blob with this content id is already stored (the idempotency short-circuit).</summary>
    /// <param name="contentId">The content-addressed id.</param>
    /// <param name="ct">Cancels the check.</param>
    /// <returns><see langword="true"/> when the blob is already present (skip the upload).</returns>
    Task<bool> ExistsAsync(Guid contentId, CancellationToken ct);

    /// <summary>
    /// Stores the content, returning whether the handling succeeded — the engine binds this into <c>dl.stored</c>,
    /// exactly as the reference's <c>handleDownload</c> returns a bool the caller branches on (store ⇒ keep the
    /// attachment; failure ⇒ warn and drop it).
    /// </summary>
    /// <param name="item">The content metadata (id, stored name, size, hash).</param>
    /// <param name="content">The byte stream to persist (positioned at the start).</param>
    /// <param name="ct">Cancels the store.</param>
    /// <returns><see langword="true"/> when the content was stored successfully; <see langword="false"/> on a handling failure.</returns>
    Task<bool> StoreAsync(StoredDownload item, Stream content, CancellationToken ct);
}
