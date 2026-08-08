using Crawldad.Contracts.Runs;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// The outcome of an admission decision (CD-3): either <see cref="Admitted"/> (the run now occupies a slot) or a rejection
/// carrying the typed <see cref="Rejection"/> the endpoint returns as <c>429</c>. Kept as one value so the caller acts on a
/// single seam — CD-16 will change what a non-admission means (queue, not reject) by changing the gate, not the endpoint.
/// </summary>
/// <param name="Admitted">Whether the run was admitted (a slot is now held for it).</param>
/// <param name="Rejection">The typed rejection to surface when not admitted; null when admitted.</param>
public readonly record struct RunAdmission(bool Admitted, RunRejection? Rejection);

/// <summary>
/// The single admission seam for starting a run (CD-3, docs/PRODUCT.md §Pv.3): under slot-based pricing the per-tenant
/// concurrent-run cap is revenue enforcement, so admission is one abstraction the whole engine funnels through — the seam
/// CD-16 replaces to turn reject-at-cap into queue-at-cap without touching the endpoint. A run holds a slot from admission
/// until it reaches a terminal state: the synchronous path releases it when the request completes; the async path hands the
/// slot's lifetime to the background executor, which releases it at finalisation (and re-occupies on a post-restart resume).
/// </summary>
public interface IRunAdmissionGate
{
    /// <summary>Admits a run for a tenant if the tenant is under its concurrent-run cap, occupying a slot when it is; at the
    /// cap it rejects. The decision is atomic per process, so two simultaneous starts at cap-1 cannot both pass.</summary>
    /// <param name="tenantId">The run's tenant (the billing subject the cap is per, CD-1).</param>
    /// <param name="runId">The run being admitted.</param>
    /// <returns>An admission granting a slot, or a rejection to surface as <c>429</c>.</returns>
    RunAdmission TryAdmit(string tenantId, Guid runId);

    /// <summary>Re-registers a run this process is about to drive as occupying a slot, <b>without</b> a cap check — the async
    /// executor calls it at drive-start so the in-memory slot count self-heals after a restart re-runs an already-admitted
    /// run (a fresh run it already occupies is a harmless no-op).</summary>
    /// <param name="tenantId">The run's tenant.</param>
    /// <param name="runId">The run being driven.</param>
    void Occupy(string tenantId, Guid runId);

    /// <summary>Frees the run's slot once it reaches a terminal state (or its process is torn down). Idempotent.</summary>
    /// <param name="tenantId">The run's tenant.</param>
    /// <param name="runId">The run whose slot to free.</param>
    void Release(string tenantId, Guid runId);
}

/// <summary>
/// The first-slice <see cref="IRunAdmissionGate"/>: an in-process, per-tenant set of the run ids currently holding a slot,
/// guarded by one lock so the check-and-occupy is atomic. The cap is the tenant's configured override
/// (<see cref="TenantDescriptor.MaxConcurrentRuns"/>) or, absent one, the global
/// <see cref="RunLimitsOptions.MaxConcurrentRunsPerTenant"/>.
/// <para>
/// <b>Race &amp; scope (documented trade-off).</b> The lock closes the ticket's race entirely <em>within a process</em> —
/// two simultaneous starts at cap-1 serialise, and the second sees the cap. Across <em>multiple instances</em> the counts
/// are not shared, so a small over-admission is possible; closing that needs a distributed lock or a durable
/// admission counter (deferred — the first slice is single-instance-authoritative, and CD-16 owns the durable queue).
/// Likewise, an async run whose durable setup fails after admission but before the executor picks it up leaks its
/// in-memory slot until the process restarts (rare, exceptional-path); the executor is the normal releaser.
/// </para>
/// </summary>
/// <param name="tenants">The tenant directory — the source of a tenant's per-tenant cap override (CD-1).</param>
/// <param name="limits">The bound resource-limit options — the global default cap.</param>
public sealed class RunAdmissionGate(TenantRegistry tenants, IOptions<RunLimitsOptions> limits) : IRunAdmissionGate
{
    /// <summary>The machine-readable rejection code returned at the cap (HTTP 429). CD-16 introduces the queued alternative.</summary>
    public const string RejectionCode = "concurrent_runs_exceeded";

    private readonly int _defaultCap = limits.Value.MaxConcurrentRunsPerTenant;
    private readonly Dictionary<string, HashSet<Guid>> _active = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public RunAdmission TryAdmit(string tenantId, Guid runId)
    {
        lock (_gate)
        {
            var slots = Slots(tenantId);
            var cap = CapFor(tenantId);
            if (slots.Count >= cap)
            {
                return new RunAdmission(false, new RunRejection(
                    RejectionCode, $"tenant '{tenantId}' is at its concurrent-run cap of {cap}"));
            }

            slots.Add(runId);
            return new RunAdmission(true, null);
        }
    }

    /// <inheritdoc />
    public void Occupy(string tenantId, Guid runId)
    {
        lock (_gate)
        {
            Slots(tenantId).Add(runId); // cap-exempt: this process is (re)driving an already-admitted run
        }
    }

    /// <inheritdoc />
    public void Release(string tenantId, Guid runId)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(tenantId, out var slots))
            {
                slots.Remove(runId);
            }
        }
    }

    /// <summary>The number of slots a tenant currently holds — for observability and tests.</summary>
    /// <param name="tenantId">The tenant to count.</param>
    /// <returns>The active-run count for the tenant.</returns>
    internal int ActiveCount(string tenantId)
    {
        lock (_gate)
        {
            return _active.TryGetValue(tenantId, out var slots) ? slots.Count : 0;
        }
    }

    private HashSet<Guid> Slots(string tenantId)
    {
        if (!_active.TryGetValue(tenantId, out var slots))
        {
            slots = [];
            _active[tenantId] = slots;
        }

        return slots;
    }

    private int CapFor(string tenantId) =>
        tenants.TryGetConcurrencyOverride(tenantId, out var overrideCap) ? overrideCap : _defaultCap;
}
