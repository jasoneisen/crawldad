using System.Text.Json;

namespace Crawldad.Contracts.Runs;

/// <summary>
/// The <c>POST /runs</c> body and Wolverine command (§10, Deliverable 4). A run executes exactly one payload, supplied
/// one of two mutually-exclusive ways (validated by <c>StartRunRequestValidator</c>): an <b>inline</b>
/// <see cref="Payload"/> document, or a pinned managed payload named by <see cref="PayloadId"/> (+ optional
/// <see cref="Revision"/>, default = head; §14.1/§14.2). Either way the resolved script is interpreted, not modelled as
/// a DTO. <see cref="Inputs"/> binds the payload's declared <c>inputs</c> and is converted to the run's value model.
/// </summary>
/// <param name="Payload">The inline Crawldad payload object (<c>crawldad</c>/<c>inputs</c>/<c>config</c>/<c>vars</c>/<c>steps</c>/<c>result</c>).
/// Absent (a <see cref="JsonValueKind.Undefined"/> value) for a pinned run.</param>
/// <param name="Inputs">The input bindings object. A <c>backend</c> input takes the wire shape
/// <c>{ "adapter": "fake", "options": { "fixture": "caphome-search" } }</c> (optionally a <c>credentialRef</c>); a
/// missing/undefined value means no inputs.</param>
/// <param name="PayloadId">The managed payload to pin and execute (§14.2). Mutually exclusive with <see cref="Payload"/>;
/// null for an inline run.</param>
/// <param name="Revision">The pinned payload's revision to execute; null pins the current head (§14.1). Only meaningful with <see cref="PayloadId"/>.</param>
/// <param name="Async">When true, execute the run in the background durable executor saga (§11): <c>POST /runs</c> returns
/// <c>202</c> with <c>{ runId, status:"running" }</c> immediately and the caller polls <c>GET /runs/{id}</c>. Default false
/// keeps the exact synchronous semantics — the run executes inline and the terminal <see cref="RunResponse"/> is returned.</param>
public sealed record StartRunRequest(JsonElement Payload, JsonElement Inputs, Guid? PayloadId = null, int? Revision = null, bool Async = false);
