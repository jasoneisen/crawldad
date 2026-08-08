using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The CD-3 per-tenant concurrent-run admission gate (limit 5, docs/PRODUCT.md §Pv.3): the single seam a run funnels
/// through at start. Proves the cap (global default + per-tenant override), that a released slot re-opens capacity, that
/// occupancy is counted per tenant, and that the cap-exempt <c>Occupy</c> (the executor's restart self-heal) and a
/// no-op <c>Release</c> behave. The atomic check-and-occupy under one lock closes the two-simultaneous-starts race within
/// a process; the HTTP/429 surfacing is covered by <c>ConcurrentRunsCapTests</c>.
/// </summary>
public class RunAdmissionGateTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

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
    public void Admits_up_to_the_cap_then_rejects_with_the_typed_code()
    {
        var gate = Gate(globalCap: 2, (TenantA, null));

        gate.TryAdmit(TenantA, Guid.NewGuid()).Admitted.ShouldBeTrue();
        gate.TryAdmit(TenantA, Guid.NewGuid()).Admitted.ShouldBeTrue();

        var rejected = gate.TryAdmit(TenantA, Guid.NewGuid());
        rejected.Admitted.ShouldBeFalse();
        rejected.Rejection!.Code.ShouldBe(RunAdmissionGate.RejectionCode);
        rejected.Rejection.Message.ShouldContain("cap of 2");
        gate.ActiveCount(TenantA).ShouldBe(2);
    }

    [Fact]
    public void Releasing_a_slot_re_opens_capacity()
    {
        var gate = Gate(globalCap: 1, (TenantA, null));
        var run1 = Guid.NewGuid();

        gate.TryAdmit(TenantA, run1).Admitted.ShouldBeTrue();
        gate.TryAdmit(TenantA, Guid.NewGuid()).Admitted.ShouldBeFalse(); // at the cap

        gate.Release(TenantA, run1);
        gate.TryAdmit(TenantA, Guid.NewGuid()).Admitted.ShouldBeTrue(); // the freed slot admits the next
        gate.ActiveCount(TenantA).ShouldBe(1);
    }

    [Fact]
    public void Occupy_registers_a_slot_without_a_cap_check()
    {
        var gate = Gate(globalCap: 1, (TenantA, null));

        // Occupy is the executor's restart self-heal — it re-registers an already-admitted run, never re-checking the cap.
        gate.Occupy(TenantA, Guid.NewGuid());
        gate.Occupy(TenantA, Guid.NewGuid());
        gate.ActiveCount(TenantA).ShouldBe(2);
    }

    [Fact]
    public void Slots_are_counted_per_tenant()
    {
        var gate = Gate(globalCap: 1, (TenantA, null), (TenantB, null));

        gate.TryAdmit(TenantA, Guid.NewGuid()).Admitted.ShouldBeTrue();
        gate.TryAdmit(TenantA, Guid.NewGuid()).Admitted.ShouldBeFalse(); // A is at its cap

        gate.TryAdmit(TenantB, Guid.NewGuid()).Admitted.ShouldBeTrue(); // B is independent
        gate.ActiveCount(TenantB).ShouldBe(1);
    }

    [Fact]
    public void A_per_tenant_override_takes_precedence_over_the_global_default()
    {
        var gate = Gate(globalCap: 1, (TenantA, 2)); // override raises A above the global default of 1

        gate.TryAdmit(TenantA, Guid.NewGuid()).Admitted.ShouldBeTrue();
        gate.TryAdmit(TenantA, Guid.NewGuid()).Admitted.ShouldBeTrue();
        gate.TryAdmit(TenantA, Guid.NewGuid()).Admitted.ShouldBeFalse(); // and caps at the override
    }

    [Fact]
    public void Releasing_an_unknown_tenant_or_run_is_a_no_op()
    {
        var gate = Gate(globalCap: 2, (TenantA, null));
        var run1 = Guid.NewGuid();
        gate.TryAdmit(TenantA, run1).Admitted.ShouldBeTrue();

        gate.Release("no-such-tenant", Guid.NewGuid()); // unknown tenant — tolerated
        gate.Release(TenantA, Guid.NewGuid());          // unknown run in a known tenant — tolerated

        gate.ActiveCount(TenantA).ShouldBe(1);          // the real slot is untouched
        gate.ActiveCount("no-such-tenant").ShouldBe(0); // never seen
    }
}
