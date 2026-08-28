using Crawldad.Api.Infrastructure.Security;

namespace Crawldad.Tests.Unit;

/// <summary>The console-write scope predicate (issue #119 PR5): a verb-specific <c>(method, route)</c> match against
/// <see cref="ConsoleWriteEndpoints.Routes"/>. Matching on both verb AND route is what lets <c>POST /payloads</c> opt into
/// the console policy while <c>GET /payloads</c> is governed by the read set, and lets <c>PUT</c>/<c>DELETE /webhooks/{name}</c>
/// opt in independently. The live wiring's admitting-set is pinned separately by the enumeration test; this covers the
/// predicate itself.</summary>
public class ConsoleWriteEndpointsTests
{
    [Fact]
    public void A_write_verb_on_a_console_write_route_is_included() =>
        ConsoleWriteEndpoints.Includes(["POST"], "/payloads/{id}/revise").ShouldBeTrue();

    [Fact]
    public void The_method_match_is_case_insensitive() =>
        ConsoleWriteEndpoints.Includes(["post"], "/tenant/keys").ShouldBeTrue();

    [Fact]
    public void Put_and_delete_on_a_shared_webhook_route_both_opt_in()
    {
        ConsoleWriteEndpoints.Includes(["PUT"], "/webhooks/{name}").ShouldBeTrue();
        ConsoleWriteEndpoints.Includes(["DELETE"], "/webhooks/{name}").ShouldBeTrue();
    }

    [Fact]
    public void A_get_on_a_console_write_route_is_excluded() =>
        ConsoleWriteEndpoints.Includes(["GET"], "/payloads").ShouldBeFalse(); // the read half is the read set's concern

    [Fact]
    public void A_write_verb_on_a_non_console_route_is_excluded() =>
        ConsoleWriteEndpoints.Includes(["POST"], "/runs").ShouldBeFalse(); // starting a run is NOT a console write

    [Fact]
    public void A_null_route_is_excluded() =>
        ConsoleWriteEndpoints.Includes(["POST"], null).ShouldBeFalse();

    [Fact]
    public void Null_methods_are_rejected() =>
        Should.Throw<ArgumentNullException>(() => ConsoleWriteEndpoints.Includes(null!, "/payloads"));
}
