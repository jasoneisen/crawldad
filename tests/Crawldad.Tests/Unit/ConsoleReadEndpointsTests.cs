using Crawldad.Api.Infrastructure.Security;

namespace Crawldad.Tests.Unit;

/// <summary>The console-read scope predicate (issue #119 PR4): a <c>(GET, route)</c> match against
/// <see cref="ConsoleReadEndpoints.Routes"/>. Matching on both verb AND route is what lets <c>GET /tenant/keys</c> opt into
/// the console policy while <c>POST /tenant/keys</c> stays key-only. The live wiring's admitting-set is pinned separately by
/// the enumeration test; this covers the predicate itself.</summary>
public class ConsoleReadEndpointsTests
{
    [Fact]
    public void A_get_on_a_console_route_is_included() =>
        ConsoleReadEndpoints.Includes(["GET"], "/tenant").ShouldBeTrue();

    [Fact]
    public void A_non_get_on_a_console_route_is_excluded() =>
        ConsoleReadEndpoints.Includes(["POST"], "/tenant/keys").ShouldBeFalse(); // the write half of a shared route stays key-only

    [Fact]
    public void A_get_on_a_non_console_route_is_excluded() =>
        ConsoleReadEndpoints.Includes(["GET"], "/runs/queue-stats").ShouldBeFalse();

    [Fact]
    public void A_null_route_is_excluded() =>
        ConsoleReadEndpoints.Includes(["GET"], null).ShouldBeFalse();

    [Fact]
    public void Null_methods_are_rejected() =>
        Should.Throw<ArgumentNullException>(() => ConsoleReadEndpoints.Includes(null!, "/tenant"));
}
