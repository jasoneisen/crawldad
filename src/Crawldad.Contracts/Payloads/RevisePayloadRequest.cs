using System.Text.Json;

namespace Crawldad.Contracts.Payloads;

/// <summary>
/// The <c>POST /payloads/{id}/revise</c> body and Wolverine command (§14.1): a new revision of a managed payload. The
/// inline <see cref="Payload"/> document is validated (JSON Schema + semantic pass) and content-hashed exactly like a
/// draft, so a persisted revision is always executable. An optional <see cref="Note"/> annotates the revision for the
/// audit trail (who/when come from event metadata + the clock seam; the actor is deferred — see the endpoint, §12).
/// </summary>
/// <param name="Payload">The revised inline Crawldad payload object.</param>
/// <param name="Note">An optional human note describing the revision.</param>
public sealed record RevisePayloadRequest(JsonElement Payload, string? Note = null);
