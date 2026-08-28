using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Tenancy;
using Crawldad.Api.Infrastructure.Security;

namespace Crawldad.Tests.Unit;

/// <summary>The registry-first / env-fallback resolution behind <c>GET /tenant</c> and <c>GET /usage</c> (issue #119 PR1).
/// A registry tenant is authoritative for its display name, tier, and slot allowance and always takes the global
/// queue-depth default (the registry has no such field); an env tenant sources them from its descriptor, each override
/// falling back to the global default; and a tenant present in <b>neither</b> resolves to an all-default profile without
/// throwing — the guard that replaces the old <c>.First()</c>, which 500'd for a registry tenant absent from env config.</summary>
public class TenantProfileResolutionTests
{
    // The global defaults every fallback lands on (RunLimitsOptions: 32 slots, 1000 queue depth).
    private static readonly RunLimitsOptions _limits = new();

    [Fact]
    public void Registry_tenant_sources_display_name_tier_and_slot_from_its_document_and_queue_from_the_global_default()
    {
        var registered = new RegistryTenant { Id = "acme", DisplayName = "Acme Corp", Actor = "ops@acme", Tier = "pro", SlotAllowance = 7 };

        var profile = TenantProfileResolution.Resolve("acme", fallbackActor: "unused", registered, descriptor: null, _limits);

        profile.TenantId.ShouldBe("acme");
        profile.DisplayName.ShouldBe("Acme Corp");         // the registry document's display name, not its actor
        profile.Tier.ShouldBe("pro");
        profile.SlotAllowance.ShouldBe(7);                 // the registry override
        profile.QueueDepthAllowance.ShouldBe(_limits.MaxQueueDepthPerTenant); // registry has no depth field → global
    }

    [Fact]
    public void Registry_tenant_without_a_tier_or_slot_override_defers_to_the_global_defaults()
    {
        var registered = new RegistryTenant { Id = "bare", DisplayName = "Bare", Actor = "a", Tier = "", SlotAllowance = null };

        var profile = TenantProfileResolution.Resolve("bare", fallbackActor: "unused", registered, descriptor: null, _limits);

        profile.DisplayName.ShouldBe("Bare");
        profile.Tier.ShouldBeNull();                       // an empty tier moniker is surfaced as "no tier"
        profile.SlotAllowance.ShouldBe(_limits.MaxConcurrentRunsPerTenant);
        profile.QueueDepthAllowance.ShouldBe(_limits.MaxQueueDepthPerTenant);
    }

    [Fact]
    public void Env_tenant_sources_actor_tier_and_both_overrides_from_its_descriptor()
    {
        var descriptor = new TenantDescriptor { Id = "beta", Actor = "beta@x", ApiKey = "0123456789abcdef", Tier = "scale", MaxConcurrentRuns = 5, MaxQueueDepth = 20 };

        var profile = TenantProfileResolution.Resolve("beta", fallbackActor: "unused", registered: null, descriptor, _limits);

        profile.TenantId.ShouldBe("beta");
        profile.DisplayName.ShouldBe("beta@x");            // an env tenant's display identity is its actor
        profile.Tier.ShouldBe("scale");
        profile.SlotAllowance.ShouldBe(5);
        profile.QueueDepthAllowance.ShouldBe(20);
    }

    [Fact]
    public void Env_tenant_without_overrides_or_a_tier_defers_to_the_global_defaults()
    {
        var descriptor = new TenantDescriptor { Id = "plain", Actor = "plain@x", ApiKey = "0123456789abcdef" };

        var profile = TenantProfileResolution.Resolve("plain", fallbackActor: "unused", registered: null, descriptor, _limits);

        profile.DisplayName.ShouldBe("plain@x");
        profile.Tier.ShouldBeNull();
        profile.SlotAllowance.ShouldBe(_limits.MaxConcurrentRunsPerTenant);
        profile.QueueDepthAllowance.ShouldBe(_limits.MaxQueueDepthPerTenant);
    }

    [Fact]
    public void A_tenant_in_neither_source_resolves_to_an_all_default_profile_without_throwing()
    {
        // Not reachable through the auth boundary (a principal always comes from the registry or env), but the guard the
        // design study flagged: whatever assumed an env tenant exists must not throw. Falls back to the principal's actor.
        var profile = TenantProfileResolution.Resolve("ghost", fallbackActor: "ghost@actor", registered: null, descriptor: null, _limits);

        profile.TenantId.ShouldBe("ghost");
        profile.DisplayName.ShouldBe("ghost@actor");
        profile.Tier.ShouldBeNull();
        profile.SlotAllowance.ShouldBe(_limits.MaxConcurrentRunsPerTenant);
        profile.QueueDepthAllowance.ShouldBe(_limits.MaxQueueDepthPerTenant);
    }

    [Fact]
    public void Slot_allowance_prefers_the_registry_override_over_an_env_override()
    {
        // A tenant present in BOTH sources (belt-and-suspenders): the registry override wins, matching the auth boundary's
        // registry-first precedence. Exercises the standalone SlotAllowance helper GET /usage calls.
        var registered = new RegistryTenant { Id = "both", DisplayName = "Both", Actor = "a", SlotAllowance = 9 };
        var descriptor = new TenantDescriptor { Id = "both", Actor = "both@x", ApiKey = "0123456789abcdef", MaxConcurrentRuns = 3 };

        TenantProfileResolution.SlotAllowance(registered, descriptor, _limits).ShouldBe(9);
    }
}
