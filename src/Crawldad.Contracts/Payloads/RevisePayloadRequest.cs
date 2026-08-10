using System.Text.Json;

namespace Crawldad.Contracts.Payloads;

/// <summary>The <c>POST /payloads/{id}/revise</c> body and Wolverine command: a new revision of a managed payload.
/// <see cref="Payload"/> is validated and content-hashed exactly like a draft, so a persisted revision is always
/// executable.</summary>
public sealed record RevisePayloadRequest(JsonElement Payload, string? Note = null);
