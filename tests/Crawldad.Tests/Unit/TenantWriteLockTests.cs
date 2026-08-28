using Crawldad.Api.Infrastructure.Security;

namespace Crawldad.Tests.Unit;

/// <summary>The stable advisory-lock key derivation (issue #119 PR5): the same tenant id must always map to the same lock
/// slot across processes (so a per-tenant advisory lock actually serializes), which <see cref="string.GetHashCode()"/>
/// would not guarantee (it is per-run randomized). The lock acquisition itself is exercised end-to-end by the guarded
/// revoke store tests.</summary>
public class TenantWriteLockTests
{
    private const string _tenant = "7f2b8c40-1111-4a2b-9c3d-0123456789ab";

    [Fact]
    public void The_key_is_deterministic_for_the_same_tenant() =>
        TenantWriteLock.StableKey(_tenant).ShouldBe(TenantWriteLock.StableKey(_tenant));

    [Fact]
    public void Different_tenants_map_to_different_keys() =>
        TenantWriteLock.StableKey("tenant-a").ShouldNotBe(TenantWriteLock.StableKey("tenant-b"));

    [Fact]
    public void A_null_tenant_is_rejected() =>
        Should.Throw<ArgumentNullException>(() => TenantWriteLock.StableKey(null!));
}
