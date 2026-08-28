using Crawldad.Api.Infrastructure.Security;

namespace Crawldad.Tests.Unit;

/// <summary>The Owner-only console scope predicate (issue #119 PR6): a verb-specific <c>(method, route)</c> match against
/// <see cref="ConsoleOwnerEndpoints.Routes"/> — the key/membership-management subset that requires the Owner role on the
/// console channel. The live wiring's admitting-set is pinned separately by the enumeration test; this covers the predicate.</summary>
public class ConsoleOwnerEndpointsTests
{
    [Fact]
    public void Key_and_membership_management_routes_are_owner_only()
    {
        ConsoleOwnerEndpoints.Includes(["POST"], "/tenant/keys").ShouldBeTrue();
        ConsoleOwnerEndpoints.Includes(["DELETE"], "/tenant/memberships/{id}").ShouldBeTrue();
        ConsoleOwnerEndpoints.Includes(["post"], "/tenant/memberships/{id}/role").ShouldBeTrue(); // case-insensitive verb
    }

    [Fact]
    public void An_operational_write_is_not_owner_only() =>
        ConsoleOwnerEndpoints.Includes(["POST"], "/payloads").ShouldBeFalse(); // drafting a payload stays Member-reachable

    [Fact]
    public void A_null_route_is_excluded() =>
        ConsoleOwnerEndpoints.Includes(["POST"], null).ShouldBeFalse();

    [Fact]
    public void Null_methods_are_rejected() =>
        Should.Throw<ArgumentNullException>(() => ConsoleOwnerEndpoints.Includes(null!, "/tenant/keys"));
}
