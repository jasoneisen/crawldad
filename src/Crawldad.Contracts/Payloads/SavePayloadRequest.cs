using System.Text.Json;

namespace Crawldad.Contracts.Payloads;

/// <summary>
/// The <c>POST /payloads</c> body and Wolverine command (§14.1): the inline Crawldad payload document to validate and
/// draft. Carried as a raw <see cref="JsonElement"/> — the payload is validated (JSON Schema + semantic pass) and
/// content-hashed, not modelled as a DTO. Its logical <c>name</c> lives inside the document (§4).
/// </summary>
/// <param name="Payload">The inline Crawldad payload object (<c>crawldad</c>/<c>name</c>/<c>config</c>/<c>steps</c>/<c>result</c>, …).</param>
public sealed record SavePayloadRequest(JsonElement Payload);
