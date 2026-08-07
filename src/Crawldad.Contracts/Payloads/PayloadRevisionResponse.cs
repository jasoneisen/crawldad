using System.Text.Json;

namespace Crawldad.Contracts.Payloads;

/// <summary>
/// The <c>GET /payloads/{id}/revisions/{revision}</c> response (§14.1): one historical revision of a managed payload,
/// reconstructed from the event stream (the event-sourced equivalent of <c>AggregateStreamAsync(id, version:N)</c>,
/// extended to also carry the script body which the metadata-only aggregate does not). The <see cref="Script"/> is the
/// stored — already credential-scrubbed (§12) — payload document, exactly the bytes a run pinned at this revision
/// executes.
/// </summary>
/// <param name="PayloadId">The payload's event-stream id.</param>
/// <param name="Revision">The revision returned.</param>
/// <param name="ScriptHash">The revision's script hash (SHA-256, lowercase hex).</param>
/// <param name="Script">The revision's payload document (the scrubbed, executable script).</param>
public sealed record PayloadRevisionResponse(Guid PayloadId, int Revision, string ScriptHash, JsonElement Script);
