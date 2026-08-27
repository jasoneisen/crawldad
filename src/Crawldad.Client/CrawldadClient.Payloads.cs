using System.Globalization;
using System.Text.Json;
using Crawldad.Contracts.Drift;
using Crawldad.Contracts.Payloads;

namespace Crawldad.Client;

/// <summary>Managed-payload surface: draft/save, revise, rename, archive, and the read side (list, get, revision, diff,
/// drift-status). Validation is inherent to save/revise — a malformed payload is a
/// <see cref="CrawldadPayloadInvalidException"/> (<c>400</c>), never a persisted-but-broken revision.</summary>
public sealed partial class CrawldadClient
{
    /// <summary>Drafts a payload (<c>POST /payloads</c>): scrubs, validates, hashes, and stores it as revision 1. The
    /// logical name lives inside the document.</summary>
    /// <param name="request">The inline payload document to draft.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The persisted payload's identity and pinned head.</returns>
    /// <exception cref="CrawldadPayloadInvalidException">The payload failed schema/semantic validation (<c>400</c>).</exception>
    public Task<PayloadResponse> SavePayloadAsync(SavePayloadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<PayloadResponse>(HttpMethod.Post, "payloads", request with { Payload = OrEmptyObject(request.Payload) }, ct);
    }

    /// <summary>Drafts a payload (<c>POST /payloads</c>) from an inline document.</summary>
    /// <param name="payload">The inline Crawldad payload document (its <c>name</c> is inside).</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The persisted payload's identity and pinned head.</returns>
    public Task<PayloadResponse> SavePayloadAsync(JsonElement payload, CancellationToken ct = default) =>
        SavePayloadAsync(new SavePayloadRequest(payload), ct);

    /// <summary>Lists every managed payload's summary row (<c>GET /payloads</c>) — metadata only, no script body.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The payload listing.</returns>
    public Task<PayloadListResponse> ListPayloadsAsync(CancellationToken ct = default) =>
        GetAsync<PayloadListResponse>("payloads", ct);

    /// <summary>Fetches a payload's current state (<c>GET /payloads/{id}</c>).</summary>
    /// <param name="payloadId">The payload id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The payload state DTO.</returns>
    /// <exception cref="CrawldadNotFoundException">No such payload for this tenant (<c>404</c>).</exception>
    public Task<PayloadResponse> GetPayloadAsync(Guid payloadId, CancellationToken ct = default) =>
        GetAsync<PayloadResponse>($"payloads/{payloadId}", ct);

    /// <summary>Fetches one historical revision's script + metadata (<c>GET /payloads/{id}/revisions/{revision}</c>).</summary>
    /// <param name="payloadId">The payload id.</param>
    /// <param name="revision">The revision number.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The revision's stored (scrubbed) script and metadata.</returns>
    /// <exception cref="CrawldadNotFoundException">Unknown payload or revision (<c>404</c>).</exception>
    public Task<PayloadRevisionResponse> GetPayloadRevisionAsync(Guid payloadId, int revision, CancellationToken ct = default) =>
        GetAsync<PayloadRevisionResponse>($"payloads/{payloadId}/revisions/{revision.ToString(CultureInfo.InvariantCulture)}", ct);

    /// <summary>Diffs two revisions (<c>GET /payloads/{id}/diff/{from}/{to}</c>): both scripts plus the minimal
    /// structural diff.</summary>
    /// <param name="payloadId">The payload id.</param>
    /// <param name="fromRevision">The base revision.</param>
    /// <param name="toRevision">The target revision.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The diff response.</returns>
    /// <exception cref="CrawldadNotFoundException">Unknown payload or either revision (<c>404</c>).</exception>
    public Task<PayloadDiffResponse> DiffPayloadAsync(Guid payloadId, int fromRevision, int toRevision, CancellationToken ct = default) =>
        GetAsync<PayloadDiffResponse>(
            $"payloads/{payloadId}/diff/{fromRevision.ToString(CultureInfo.InvariantCulture)}/{toRevision.ToString(CultureInfo.InvariantCulture)}", ct);

    /// <summary>Appends a new script revision (<c>POST /payloads/{id}/revise</c>), running the same scrub-then-validate
    /// gate as a draft.</summary>
    /// <param name="payloadId">The payload id.</param>
    /// <param name="request">The new script (and optional note).</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The payload's new head.</returns>
    /// <exception cref="CrawldadNotFoundException">No such payload for this tenant (<c>404</c>).</exception>
    /// <exception cref="CrawldadPayloadInvalidException">The payload is archived or the new script failed validation (<c>400</c>).</exception>
    public Task<PayloadResponse> RevisePayloadAsync(Guid payloadId, RevisePayloadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendJsonAsync<PayloadResponse>(HttpMethod.Post, $"payloads/{payloadId}/revise", request with { Payload = OrEmptyObject(request.Payload) }, ct);
    }

    /// <summary>Appends a new script revision (<c>POST /payloads/{id}/revise</c>).</summary>
    /// <param name="payloadId">The payload id.</param>
    /// <param name="payload">The new inline payload document.</param>
    /// <param name="note">An optional revision note.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The payload's new head.</returns>
    public Task<PayloadResponse> RevisePayloadAsync(Guid payloadId, JsonElement payload, string? note = null, CancellationToken ct = default) =>
        RevisePayloadAsync(payloadId, new RevisePayloadRequest(payload, note), ct);

    /// <summary>Renames a payload (<c>POST /payloads/{id}/rename</c>): metadata only — the head revision advances, the
    /// script hash is unchanged.</summary>
    /// <param name="payloadId">The payload id.</param>
    /// <param name="name">The new name (must be non-empty).</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The payload's new head.</returns>
    /// <exception cref="CrawldadNotFoundException">No such payload for this tenant (<c>404</c>).</exception>
    /// <exception cref="CrawldadPayloadInvalidException">The payload is archived, or the name is empty (<c>400</c>).</exception>
    public Task<PayloadResponse> RenamePayloadAsync(Guid payloadId, string name, CancellationToken ct = default) =>
        SendJsonAsync<PayloadResponse>(HttpMethod.Post, $"payloads/{payloadId}/rename", new RenamePayloadRequest(name), ct);

    /// <summary>Archives a payload (<c>POST /payloads/{id}/archive</c>): a terminal lifecycle change that blocks further
    /// revise/rename/archive and new pinned runs.</summary>
    /// <param name="payloadId">The payload id.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The payload's new head (now archived).</returns>
    /// <exception cref="CrawldadNotFoundException">No such payload for this tenant (<c>404</c>).</exception>
    /// <exception cref="CrawldadPayloadInvalidException">The payload is already archived (<c>400</c>).</exception>
    public Task<PayloadResponse> ArchivePayloadAsync(Guid payloadId, CancellationToken ct = default) =>
        PostAsync<PayloadResponse>($"payloads/{payloadId}/archive", ct);

    /// <summary>Reads a payload canary's per-selector drift assessment (<c>GET /payloads/{id}/drift-status</c>).</summary>
    /// <param name="payloadId">The payload id.</param>
    /// <param name="threshold">The optional newly-missing-selector tolerance before <c>drifted</c> (<c>?threshold=N</c>).</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The drift status.</returns>
    /// <exception cref="CrawldadNotFoundException">No such payload for this tenant (<c>404</c>).</exception>
    public Task<PayloadDriftStatus> GetPayloadDriftStatusAsync(Guid payloadId, int? threshold = null, CancellationToken ct = default)
    {
        var path = threshold is null
            ? $"payloads/{payloadId}/drift-status"
            : $"payloads/{payloadId}/drift-status?threshold={threshold.Value.ToString(CultureInfo.InvariantCulture)}";
        return GetAsync<PayloadDriftStatus>(path, ct);
    }
}
