using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The configured-tenant directory (CD-1): a presented API key is hash-compared (fixed-time) to a configured tenant, and a
/// malformed configuration fails loudly at construction so the host never silently admits or rejects requests. Also the
/// authority on the tenant id set the out-of-request recovery scan fans out over.
/// </summary>
public class TenantRegistryTests
{
    private static TenantRegistry Registry(params TenantDescriptor[] tenants) =>
        new(Options.Create(new TenantOptions { Tenants = tenants }));

    private static TenantDescriptor Tenant(string id = "t1", string key = "key-0123456789abcdef", string actor = "actor@x", int? maxConcurrentRuns = null, int? maxQueueDepth = null) =>
        new() { Id = id, ApiKey = key, Actor = actor, MaxConcurrentRuns = maxConcurrentRuns, MaxQueueDepth = maxQueueDepth };

    [Fact]
    public void Authenticates_a_configured_key_to_its_tenant_and_actor()
    {
        var registry = Registry(Tenant("alpha", "alpha-key-0123456789", "alpha@crawldad.test"));

        registry.TryAuthenticate("alpha-key-0123456789", out var tenant).ShouldBeTrue();
        tenant!.Value.Id.ShouldBe("alpha");
        tenant.Value.Actor.ShouldBe("alpha@crawldad.test");
    }

    [Fact]
    public void Rejects_an_unknown_key()
    {
        var registry = Registry(Tenant());

        registry.TryAuthenticate("some-other-key-0123456789", out var tenant).ShouldBeFalse();
        tenant.ShouldBeNull();
    }

    [Fact]
    public void Exposes_every_configured_tenant_id()
    {
        var registry = Registry(
            Tenant("alpha", "alpha-key-0123456789"),
            Tenant("beta", "beta-key-0123456789"));

        registry.TenantIds.ShouldBe(["alpha", "beta"], ignoreOrder: true);
    }

    [Fact]
    public void Rejects_a_tenant_with_no_id() =>
        Should.Throw<InvalidOperationException>(() => Registry(Tenant(id: "  ")));

    [Fact]
    public void Rejects_a_tenant_id_containing_a_colon() =>
        // CD-6: a ':' in a tenant id would make the Secrets:{tenant}:{ref} vault namespace ambiguous.
        Should.Throw<InvalidOperationException>(() => Registry(Tenant(id: "acme:evil")));

    [Fact]
    public void Rejects_a_tenant_with_no_actor() =>
        Should.Throw<InvalidOperationException>(() => Registry(Tenant(actor: "")));

    [Fact]
    public void Rejects_an_api_key_below_the_minimum_length() =>
        Should.Throw<InvalidOperationException>(() => Registry(Tenant(key: "too-short")));

    [Fact]
    public void Rejects_a_duplicate_tenant_id() =>
        Should.Throw<InvalidOperationException>(() => Registry(
            Tenant(id: "dup", key: "first-key-0123456789"),
            Tenant(id: "dup", key: "second-key-0123456789")));

    [Fact]
    public void Rejects_two_tenants_sharing_an_api_key() =>
        Should.Throw<InvalidOperationException>(() => Registry(
            Tenant(id: "alpha", key: "shared-key-0123456789"),
            Tenant(id: "beta", key: "shared-key-0123456789")));

    [Fact]
    public void Rejects_a_concurrent_run_override_below_one() => // CD-3: a 0/negative slot cap is a boot-time misconfiguration
        Should.Throw<InvalidOperationException>(() => Registry(Tenant(maxConcurrentRuns: 0)));

    [Fact]
    public void Resolves_a_queue_depth_override() // CD-16: a tenant's per-tier at-cap wait room
    {
        var registry = Registry(Tenant(id: "alpha", key: "alpha-key-0123456789", maxQueueDepth: 10));

        registry.TryGetQueueDepthOverride("alpha", out var depth).ShouldBeTrue();
        depth.ShouldBe(10);
    }

    [Fact]
    public void Defers_when_a_tenant_sets_no_queue_depth_override()
    {
        var registry = Registry(Tenant(id: "alpha", key: "alpha-key-0123456789", maxQueueDepth: null));

        registry.TryGetQueueDepthOverride("alpha", out var depth).ShouldBeFalse(); // falls back to the global default
        depth.ShouldBe(0);
    }

    [Fact]
    public void Defers_for_an_unknown_tenant_queue_depth_override()
    {
        var registry = Registry(Tenant(id: "alpha", key: "alpha-key-0123456789", maxQueueDepth: 10));

        registry.TryGetQueueDepthOverride("no-such-tenant", out var depth).ShouldBeFalse();
        depth.ShouldBe(0);
    }

    [Fact]
    public void Rejects_a_queue_depth_override_below_one() => // CD-16: a 0/negative queue depth is a boot-time misconfiguration
        Should.Throw<InvalidOperationException>(() => Registry(Tenant(maxQueueDepth: 0)));
}
