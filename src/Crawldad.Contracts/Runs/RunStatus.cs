namespace Crawldad.Contracts.Runs;

/// <summary>The disposition of a run (§10/§11). Serialized camelCase (<c>"queued"</c>/<c>"running"</c>/<c>"succeeded"</c>/
/// <c>"failed"</c>/<c>"cancelled"</c>) via <see cref="ContractsJson"/>. The synchronous <c>POST /runs</c> response is only ever a
/// terminal status; <see cref="Running"/> is reported by the async path (the immediate <c>202</c> and <c>GET /runs/{id}</c> while
/// the executor saga is still driving the run, §11), and <see cref="Cancelled"/> once a cooperative cancel has torn the run down.
/// <see cref="Queued"/> is reported when a run is admitted at the tenant's concurrent-run cap and waits for a slot (CD-16,
/// docs/PRODUCT.md §Pv.3) — the at-cap alternative to a 429; a queued sync run is upgraded to this async surface.</summary>
public enum RunStatus
{
    /// <summary>The run is executing in the background (async mode, §11) — its <c>result</c> is not available yet; poll <c>GET /runs/{id}</c>.</summary>
    Running,

    /// <summary>The run completed and produced a <c>result</c>.</summary>
    Succeeded,

    /// <summary>The run raised a typed failure (§8.3); the response carries <c>failure</c> instead of <c>result</c>.</summary>
    Failed,

    /// <summary>A cooperative cancel (§11) tore the run down between steps; the response carries whatever <c>partial</c> result was safe.</summary>
    Cancelled,

    /// <summary>The run is admitted but waiting behind the tenant's concurrent-run cap for a free slot (CD-16): it holds no slot
    /// yet, carries a 1-based queue <c>position</c>, and starts automatically (transitioning to <see cref="Running"/>) when a
    /// slot frees. Reported by the <c>202</c> at admission and by <c>GET /runs/{id}</c> until promotion (docs/PRODUCT.md §Pv.3).
    /// <para>Declared last so its ordinal is additive — existing persisted run-progress rows keep their stored integer
    /// disposition across the CD-16 deploy.</para></summary>
    Queued,
}
