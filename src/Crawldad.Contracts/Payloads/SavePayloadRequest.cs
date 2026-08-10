using System.Text.Json;

namespace Crawldad.Contracts.Payloads;

/// <summary>The <c>POST /payloads</c> body and Wolverine command: the inline Crawldad payload document to validate
/// and draft, carried as a raw <see cref="JsonElement"/> (validated and content-hashed, not modelled as a DTO) —
/// its logical <c>name</c> lives inside the document.</summary>
public sealed record SavePayloadRequest(JsonElement Payload);
