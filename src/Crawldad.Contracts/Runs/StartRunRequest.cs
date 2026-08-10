using System.Text.Json;

namespace Crawldad.Contracts.Runs;

/// <summary>The <c>POST /runs</c> body and Wolverine command: executes exactly one payload, supplied one of two
/// mutually-exclusive ways — an inline <see cref="Payload"/> document, or a pinned managed payload named by
/// <see cref="PayloadId"/> (+ optional <see cref="Revision"/>, default head).</summary>
public sealed record StartRunRequest(JsonElement Payload, JsonElement Inputs, Guid? PayloadId = null, int? Revision = null, bool Async = false);
