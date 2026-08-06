using System.Text.Json;
using Crawldad.Contracts.Runs;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>
/// The interpreter's result for one run: either a serialised <see cref="Result"/> (success) or a
/// <see cref="Failure"/> (§8.3), plus the <see cref="Stats"/>. The endpoint stamps the run id and maps this to the
/// §10 <see cref="RunResponse"/> and the persisted trace events.
/// </summary>
/// <param name="Status">Succeeded or failed.</param>
/// <param name="Result">The evaluated <c>result</c> JSON on success; null on failure.</param>
/// <param name="Failure">The typed failure on failure; null on success.</param>
/// <param name="Stats">The run counters.</param>
/// <param name="Events">The interpreter's trace events (<c>LogEmitted</c>/<c>RunAttemptFailed</c>) in occurrence order,
/// which the endpoint appends to the run stream between <c>RunStarted</c> and the terminal event — accumulated across
/// retry attempts and returned on both success and failure (§13).</param>
internal sealed record RunOutcome(RunStatus Status, JsonElement? Result, RunFailureDetail? Failure, RunStats Stats, IReadOnlyList<object> Events);
