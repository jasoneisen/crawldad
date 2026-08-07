using System.Text.Json;

namespace Crawldad.Contracts.Runs;

/// <summary>
/// The <c>POST /runs/{id}/replay</c> body (§13 replay): re-executes a historical run's <b>pinned payload revision</b>
/// (§14.1/§14.2) — the exact revision + script hash the original run recorded, so the script is guaranteed identical and
/// drift is comparable. Because inputs are <b>never persisted as values</b> (§12), the caller <b>resupplies</b> them here;
/// the pinned revision + hash guarantee the same script, and re-running with fresh inputs is the deliberate v1 replay
/// contract. Only a run that pinned a managed payload is replayable — an inline run (whose script was never stored as a
/// revision) is rejected with a typed <see cref="RunRejection"/>. The response is the same shape as <c>POST /runs</c>
/// (a synchronous <see cref="RunResponse"/> or, with <see cref="Async"/>, a <c>202</c> <see cref="RunStateResponse"/>).
/// </summary>
/// <param name="Inputs">The input bindings to re-run the pinned revision with (a missing/undefined value means no inputs).
/// The same shape as <c>POST /runs</c>'s <c>inputs</c> (a <c>backend</c> binding plus the payload's declared inputs).</param>
/// <param name="Async">When true, replay in the background durable executor saga (§11) and return <c>202</c>; default false
/// replays synchronously and returns the terminal <see cref="RunResponse"/>.</param>
public sealed record ReplayRunRequest(JsonElement Inputs, bool Async = false);
