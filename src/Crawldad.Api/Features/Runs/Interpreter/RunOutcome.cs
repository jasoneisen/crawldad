using System.Text.Json;
using Crawldad.Contracts.Runs;

namespace Crawldad.Api.Features.Runs.Interpreter;

/// <summary>The interpreter's result for one run: a serialised <see cref="Result"/>, a <see cref="Failure"/>, or a
/// <see cref="Partial"/> (cooperative cancel), plus <see cref="Stats"/>. <see cref="Events"/> holds the synchronous
/// path's accumulated coarse events; the durable executor path emits events live instead, so it stays empty there.</summary>
internal sealed record RunOutcome(RunStatus Status, JsonElement? Result, RunFailureDetail? Failure, JsonElement? Partial, RunStats Stats, IReadOnlyList<object> Events);
