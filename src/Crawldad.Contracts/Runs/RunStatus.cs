namespace Crawldad.Contracts.Runs;

/// <summary>The disposition of a run. Serialized camelCase via <see cref="ContractsJson"/>. The synchronous
/// <c>POST /runs</c> response is only ever a terminal status; <see cref="Running"/>/<see cref="Queued"/> are reported
/// by the async control surface (<c>202</c> and <c>GET /runs/{id}</c>).</summary>
public enum RunStatus
{
    /// <summary>The run is executing in the background — its <c>result</c> is not available yet; poll <c>GET /runs/{id}</c>.</summary>
    Running,

    /// <summary>The run completed and produced a <c>result</c>.</summary>
    Succeeded,

    /// <summary>The run raised a typed failure; the response carries <c>failure</c> instead of <c>result</c>.</summary>
    Failed,

    /// <summary>A cooperative cancel tore the run down between steps; the response carries whatever <c>partial</c> result was safe.</summary>
    Cancelled,

    /// <summary>Admitted but waiting behind the tenant's concurrent-run cap for a free slot: holds no slot yet, carries
    /// a 1-based queue <c>position</c>, and transitions to <see cref="Running"/> when a slot frees. Declared last so its
    /// ordinal is additive — persisted run-progress rows keep their stored integer disposition across deploys.</summary>
    Queued,
}
