using Crawldad.Api.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Crawldad.Api.Features.Runs;

/// <summary>The single admission seam for starting a run: a run holds a slot from admission until it reaches a terminal
/// state (the sync path releases it at request-end; the async path hands its lifetime to the executor). The gate answers
/// only "is a slot free?" — a run that cannot be admitted is queued, not rejected, by <see cref="RunQueue"/>.</summary>
public interface IRunAdmissionGate
{
    /// <summary>Resolves the tenant's per-tenant cap so the following <see cref="TryAdmit"/>/<see cref="HasCapacity"/> read
    /// it correctly. The admission call sites await this before admitting, so a registry tenant's slot allowance is honoured
    /// on the background promotion path (which can run long after — or without — a recent auth), not just at request time.</summary>
    Task PrimeAsync(string tenantId, CancellationToken ct);

    /// <summary>Atomically admits a run if the tenant is under its cap and this run is not already counted, occupying a slot
    /// when so. Check-and-occupy is atomic per process, so two simultaneous starts at cap-1 cannot both pass, and refusing an
    /// already-counted run means two concurrent promotions cannot both claim it. False at the cap.</summary>
    bool TryAdmit(string tenantId, Guid runId);

    /// <summary>Re-registers a run this process is about to drive as occupying a slot, <b>without</b> a cap check — the async
    /// executor calls it at drive-start so the in-memory slot count self-heals after a restart re-runs an already-admitted
    /// run (a fresh run it already occupies is a harmless no-op).</summary>
    void Occupy(string tenantId, Guid runId);

    /// <summary>Frees the run's slot once it reaches a terminal state (or its process is torn down). Idempotent.</summary>
    void Release(string tenantId, Guid runId);

    /// <summary>Whether the tenant holds fewer slots than its cap right now. A racy hint (can change the instant after),
    /// used only to decide whether an enqueue should nudge a promotion — so a run that enqueues in the narrow window just
    /// after the last slot frees is not stranded behind idle capacity. False when the cap is full (no spurious trigger).</summary>
    bool HasCapacity(string tenantId);
}

/// <summary>The in-process, per-tenant set of run ids holding a slot, guarded by one lock so check-and-occupy is atomic;
/// the cap is a per-tenant override or the global default. Counts are per-process only — a multi-instance deployment can
/// transiently over-admit (cross-instance, or ~2× during restart catch-up) until runs finalise or self-heal via <see cref="Occupy"/>.</summary>
public sealed class RunAdmissionGate(ITenantConcurrencyOverrides tenants, IOptions<RunLimitsOptions> limits) : IRunAdmissionGate
{
    private readonly int _defaultCap = limits.Value.MaxConcurrentRunsPerTenant;
    private readonly Dictionary<string, HashSet<Guid>> _active = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public Task PrimeAsync(string tenantId, CancellationToken ct) => tenants.PrimeAsync(tenantId, ct);

    /// <inheritdoc />
    public bool TryAdmit(string tenantId, Guid runId)
    {
        lock (_gate)
        {
            var slots = Slots(tenantId);
            if (slots.Count >= CapFor(tenantId) || slots.Contains(runId))
            {
                return false; // at the cap → queue; already counted → a concurrent promotion already claimed this run
            }

            slots.Add(runId);
            return true;
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

    /// <inheritdoc />
    public bool HasCapacity(string tenantId)
    {
        lock (_gate)
        {
            return (_active.TryGetValue(tenantId, out var slots) ? slots.Count : 0) < CapFor(tenantId);
        }
    }

    /// <summary>The number of slots a tenant currently holds — for observability and tests.</summary>
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
