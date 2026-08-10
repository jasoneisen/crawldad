using System.Text.Json;

namespace Crawldad.Contracts.Runs;

/// <summary>The <c>POST /runs/{id}/replay</c> body: re-executes a run's pinned payload revision and script hash.
/// Inputs are never persisted, so the caller resupplies them here. Only a run that pinned a managed payload is
/// replayable — an inline run is rejected with a typed <see cref="RunRejection"/>.</summary>
public sealed record ReplayRunRequest(JsonElement Inputs, bool Async = false);
