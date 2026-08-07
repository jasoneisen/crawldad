using System.Text.Json;
using Crawldad.Contracts.Runs;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>
/// The interpreter's result for one run: a serialised <see cref="Result"/> (success), a <see cref="Failure"/> (§8.3), or
/// a <see cref="Partial"/> (a cooperative cancel, §11), plus the <see cref="Stats"/>. The endpoint/executor stamps the run
/// id and maps this to the §10/§11 response and the persisted trace events.
/// </summary>
/// <param name="Status">Succeeded, failed, or cancelled.</param>
/// <param name="Result">The evaluated <c>result</c> JSON on success; null otherwise.</param>
/// <param name="Failure">The typed failure on failure; null otherwise.</param>
/// <param name="Partial">The best-effort result-so-far salvaged when cancelled (§11); null otherwise (or if none was safe).</param>
/// <param name="Stats">The run counters.</param>
/// <param name="Events">The interpreter's coarse trace events (<c>LogEmitted</c>/<c>RunAttemptFailed</c>) accumulated on the
/// <b>synchronous</b> path for the endpoint to append between <c>RunStarted</c> and the terminal event (in occurrence order,
/// across retries). On the durable executor path an observer emits every trace event live instead (§13), so this list is
/// empty there — the executor appends only the terminal event at finalisation.</param>
internal sealed record RunOutcome(RunStatus Status, JsonElement? Result, RunFailureDetail? Failure, JsonElement? Partial, RunStats Stats, IReadOnlyList<object> Events);
