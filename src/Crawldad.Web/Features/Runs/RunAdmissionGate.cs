using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// The single admission seam for starting a run (CD-3/CD-16, docs/PRODUCT.md §Pv.3): under slot-based pricing the per-tenant
/// concurrent-run cap is revenue enforcement, so admission is one abstraction the whole engine funnels through. A run holds a
/// slot from admission until it reaches a terminal state: the synchronous path releases it when the request completes; the
/// async path hands the slot's lifetime to the background executor, which releases it at finalisation (and re-occupies on a
/// post-restart resume). Since CD-16 the gate no longer decides the at-cap <em>response</em> — a run that cannot be admitted is
/// queued, not rejected (<see cref="RunQueue"/>); the gate answers only the atomic question "is a slot free for this run?".
/// </summary>
public interface IRunAdmissionGate
{
    /// <summary>Atomically admits a run for a tenant if the tenant is under its concurrent-run cap and this run is not already
    /// counted, occupying a slot when it is. The check-and-occupy is atomic per process, so two simultaneous starts at cap-1
    /// cannot both pass, and two concurrent promotions cannot both claim the same run (a run it already counts is refused, so
    /// promotion needs no separate lock). At the cap it returns false and the caller queues the run (CD-16).</summary>
    /// <param name="tenantId">The run's tenant (the billing subject the cap is per, CD-1).</param>
    /// <param name="runId">The run being admitted.</param>
    /// <returns>True when a slot was granted (newly occupied for this run); false at the cap or when the run already holds one.</returns>
    bool TryAdmit(string tenantId, Guid runId);

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

    /// <summary>Whether the tenant currently holds fewer slots than its cap — i.e. a slot is free right now. A racy hint (the
    /// count can change the instant after), used only to decide whether an enqueue should nudge a promotion (CD-16): a run that
    /// enqueues in the narrow window just after the last slot frees would otherwise sit behind idle capacity with no pending
    /// trigger. When the cap is full (the common at-cap enqueue) this is false, so no spurious trigger is published.</summary>
    /// <param name="tenantId">The tenant to check.</param>
    /// <returns>True when the tenant is under its cap (a slot is free).</returns>
    bool HasCapacity(string tenantId);
}

/// <summary>
/// The in-process, per-tenant set of run ids currently holding a slot, guarded by one lock so check-and-occupy is atomic. The
/// cap is the tenant's configured override (<see cref="TenantDescriptor.MaxConcurrentRuns"/>) or, absent one, the global
/// <see cref="RunLimitsOptions.MaxConcurrentRunsPerTenant"/>. <see cref="TryAdmit"/> refuses a run it already counts, so it
/// doubles as the atomic reservation point for both fresh admission and queue promotion (<see cref="RunQueue"/>) without a
/// second lock.
/// <para>
/// <b>Race &amp; scope (documented trade-off).</b> The lock closes the ticket's race entirely <em>within a process</em> — two
/// simultaneous starts at cap-1 serialise, and the second sees the cap. Two remaining transients, both single-instance-benign
/// and self-correcting, are accepted exactly as CD-3 framed them:
/// <list type="bullet">
/// <item><b>Cross-instance.</b> Counts are not shared between instances, so a small over-admission is possible until runs
/// finalise; closing it needs a distributed lock or a durable admission counter (deferred — CD-16's durable queue governs
/// <em>order</em> and durability, not cross-instance slot arithmetic).</item>
/// <item><b>Restart catch-up.</b> After a restart the set is empty and refills as the recovery scan re-drives running runs
/// (<c>Occupy</c>) and promotes queued ones; a promotion that reserves a slot before a resuming run has re-occupied its own can
/// transiently exceed the cap (~2×) until both are counted, after which finalisation restores the invariant. FIFO order and
/// durability are unaffected — only momentary occupancy.</item>
/// </list>
/// </para>
/// </summary>
/// <param name="tenants">The tenant directory — the source of a tenant's per-tenant cap override (CD-1).</param>
/// <param name="limits">The bound resource-limit options — the global default cap.</param>
public sealed class RunAdmissionGate(TenantRegistry tenants, IOptions<RunLimitsOptions> limits) : IRunAdmissionGate
{
    private readonly int _defaultCap = limits.Value.MaxConcurrentRunsPerTenant;
    private readonly Dictionary<string, HashSet<Guid>> _active = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public bool TryAdmit(string tenantId, Guid runId)
    {
        lock (_gate)
        {
            var slots = Slots(tenantId);
            if (slots.Count >= CapFor(tenantId) || slots.Contains(runId))
            {
                return false; // at the cap → queue (CD-16); already counted → a concurrent promotion already claimed this run
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
