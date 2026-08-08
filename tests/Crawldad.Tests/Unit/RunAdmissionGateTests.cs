using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The CD-3/CD-16 per-tenant concurrent-run admission gate: the single atomic seam a run funnels through at start. Proves the
/// cap (global default + per-tenant override), that a released slot re-opens capacity, that occupancy is counted per tenant,
/// that the cap-exempt <c>Occupy</c> (the executor's restart self-heal) and a no-op <c>Release</c> behave, and — the CD-16
/// addition — that <c>TryAdmit</c> refuses a run it already counts (so two concurrent promotions cannot both claim the same
/// run). Since CD-16 the gate no longer decides the at-cap response (queue vs 429) — it answers only "is a slot free?"; the
/// HTTP queue-at-cap surface is covered by <c>ConcurrentRunsCapTests</c>/<c>SlotQueueTests</c>.
/// </summary>
public class RunAdmissionGateTests
{
    private const string _tenantA = "tenant-a";
    private const string _tenantB = "tenant-b";

    private static RunAdmissionGate Gate(int globalCap, params (string Id, int? Override)[] tenants) =>
        new(Registry(tenants), Options.Create(new RunLimitsOptions { MaxConcurrentRunsPerTenant = globalCap }));

    private static TenantRegistry Registry((string Id, int? Override)[] tenants)
    {
        var options = new TenantOptions
        {
            Tenants = [.. tenants.Select((t, i) => new TenantDescriptor
            {
                Id = t.Id,
                ApiKey = $"admission-key-{i}-0123456789abcdef",
                Actor = $"{t.Id}@crawldad.test",
                MaxConcurrentRuns = t.Override,
            })],
        };
        return new TenantRegistry(Options.Create(options));
    }

    [Fact]
    public void Admits_up_to_the_cap_then_refuses()
    {
        var gate = Gate(globalCap: 2, (_tenantA, null));

        gate.TryAdmit(_tenantA, Guid.NewGuid()).ShouldBeTrue();
        gate.TryAdmit(_tenantA, Guid.NewGuid()).ShouldBeTrue();

        gate.TryAdmit(_tenantA, Guid.NewGuid()).ShouldBeFalse(); // at the cap → the caller queues (CD-16), not a rejection
        gate.ActiveCount(_tenantA).ShouldBe(2);
    }

    [Fact]
    public void Refuses_a_run_it_already_counts()
    {
        var gate = Gate(globalCap: 2, (_tenantA, null));
        var run = Guid.NewGuid();

        gate.TryAdmit(_tenantA, run).ShouldBeTrue();
        gate.TryAdmit(_tenantA, run).ShouldBeFalse(); // already counted — a second (concurrent promotion) claim is refused
        gate.ActiveCount(_tenantA).ShouldBe(1);       // and the run is counted exactly once, not twice
    }

    [Fact]
    public void Reports_free_capacity_until_the_cap_is_reached() // CD-16: the enqueue-promotion nudge hint
    {
        var gate = Gate(globalCap: 1, (_tenantA, null));

        gate.HasCapacity(_tenantA).ShouldBeTrue(); // empty — a slot is free
        gate.TryAdmit(_tenantA, Guid.NewGuid()).ShouldBeTrue();
        gate.HasCapacity(_tenantA).ShouldBeFalse(); // at the cap — no free slot
    }

    [Fact]
    public void Releasing_a_slot_re_opens_capacity()
    {
        var gate = Gate(globalCap: 1, (_tenantA, null));
        var run1 = Guid.NewGuid();

        gate.TryAdmit(_tenantA, run1).ShouldBeTrue();
        gate.TryAdmit(_tenantA, Guid.NewGuid()).ShouldBeFalse(); // at the cap

        gate.Release(_tenantA, run1);
        gate.TryAdmit(_tenantA, Guid.NewGuid()).ShouldBeTrue(); // the freed slot admits the next
        gate.ActiveCount(_tenantA).ShouldBe(1);
    }

    [Fact]
    public void Occupy_registers_a_slot_without_a_cap_check()
    {
        var gate = Gate(globalCap: 1, (_tenantA, null));

        // Occupy is the executor's restart self-heal — it re-registers an already-admitted run, never re-checking the cap.
        gate.Occupy(_tenantA, Guid.NewGuid());
        gate.Occupy(_tenantA, Guid.NewGuid());
        gate.ActiveCount(_tenantA).ShouldBe(2);
    }

    [Fact]
    public void Slots_are_counted_per_tenant()
    {
        var gate = Gate(globalCap: 1, (_tenantA, null), (_tenantB, null));

        gate.TryAdmit(_tenantA, Guid.NewGuid()).ShouldBeTrue();
        gate.TryAdmit(_tenantA, Guid.NewGuid()).ShouldBeFalse(); // A is at its cap

        gate.TryAdmit(_tenantB, Guid.NewGuid()).ShouldBeTrue(); // B is independent
        gate.ActiveCount(_tenantB).ShouldBe(1);
    }

    [Fact]
    public void A_per_tenant_override_takes_precedence_over_the_global_default()
    {
        var gate = Gate(globalCap: 1, (_tenantA, 2)); // override raises A above the global default of 1

        gate.TryAdmit(_tenantA, Guid.NewGuid()).ShouldBeTrue();
        gate.TryAdmit(_tenantA, Guid.NewGuid()).ShouldBeTrue();
        gate.TryAdmit(_tenantA, Guid.NewGuid()).ShouldBeFalse(); // and caps at the override
    }

    [Fact]
    public void Releasing_an_unknown_tenant_or_run_is_a_no_op()
    {
        var gate = Gate(globalCap: 2, (_tenantA, null));
        var run1 = Guid.NewGuid();
        gate.TryAdmit(_tenantA, run1).ShouldBeTrue();

        gate.Release("no-such-tenant", Guid.NewGuid()); // unknown tenant — tolerated
        gate.Release(_tenantA, Guid.NewGuid());          // unknown run in a known tenant — tolerated

        gate.ActiveCount(_tenantA).ShouldBe(1);          // the real slot is untouched
        gate.ActiveCount("no-such-tenant").ShouldBe(0); // never seen
    }
}
