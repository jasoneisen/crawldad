using Crawldad.Api.Infrastructure.Security;

namespace Crawldad.Tests.Unit;

/// <summary>The provisioning scope enumeration (issue #119 PR7): exactly <c>POST /provisioning/tenants</c> is a provisioning
/// route — verb-specific, route-specific, null-safe. The live wiring is pinned by the integration enumeration test.</summary>
public class ProvisioningEndpointsTests
{
    [Fact]
    public void The_provision_route_with_post_is_included() =>
        ProvisioningEndpoints.Includes(["POST"], ProvisioningEndpoints.ProvisionRoute).ShouldBeTrue();

    [Fact]
    public void Another_verb_on_the_provision_route_is_not_included() =>
        ProvisioningEndpoints.Includes(["GET"], ProvisioningEndpoints.ProvisionRoute).ShouldBeFalse();

    [Fact]
    public void A_different_route_is_not_included() =>
        ProvisioningEndpoints.Includes(["POST"], "/tenant/memberships").ShouldBeFalse();

    [Fact]
    public void A_null_route_is_not_included() =>
        ProvisioningEndpoints.Includes(["POST"], null).ShouldBeFalse();

    [Fact]
    public void The_method_match_is_case_insensitive() =>
        ProvisioningEndpoints.Includes(["post"], ProvisioningEndpoints.ProvisionRoute).ShouldBeTrue();
}
