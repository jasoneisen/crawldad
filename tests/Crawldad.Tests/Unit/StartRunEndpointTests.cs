using System.Text.Json;
using Crawldad.Web.Features.Runs;

namespace Crawldad.Tests.Unit;

/// <summary>The two request-thread reads of an UNVALIDATED inline payload that run BEFORE the interpreter (so they cannot
/// raise a classified run failure): both are total — a missing or wrong-kinded field falls back to a default rather than
/// faulting the request (issue #48). The interpreter then classifies any deeper malformation as a run failure.</summary>
public class StartRunEndpointTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void PayloadName_is_the_string_name_or_unnamed()
    {
        StartRunEndpoint.PayloadName(Parse("""{ "name": "search" }""")).ShouldBe("search");
        StartRunEndpoint.PayloadName(Parse("{}")).ShouldBe("unnamed");             // absent
        StartRunEndpoint.PayloadName(Parse("""{ "name": 5 }""")).ShouldBe("unnamed"); // wrong kind — total, never throws
    }

    [Fact]
    public void ReadDeadlineMs_is_the_configured_deadline_or_the_default()
    {
        StartRunEndpoint.ReadDeadlineMs(Parse("""{ "config": { "deadlineMs": 5000 } }""")).ShouldBe(5000);
        StartRunEndpoint.ReadDeadlineMs(Parse("""{ "config": {} }""")).ShouldBe(StartRunEndpoint.DefaultDeadlineMs);        // deadlineMs absent
        StartRunEndpoint.ReadDeadlineMs(Parse("{}")).ShouldBe(StartRunEndpoint.DefaultDeadlineMs);                          // config absent
        StartRunEndpoint.ReadDeadlineMs(Parse("""{ "config": 5 }""")).ShouldBe(StartRunEndpoint.DefaultDeadlineMs);         // config not an object
        StartRunEndpoint.ReadDeadlineMs(Parse("""{ "config": { "deadlineMs": "x" } }""")).ShouldBe(StartRunEndpoint.DefaultDeadlineMs); // not a number
        StartRunEndpoint.ReadDeadlineMs(Parse("""{ "config": { "deadlineMs": 9999999999 } }""")).ShouldBe(StartRunEndpoint.DefaultDeadlineMs); // out of Int32 range
    }
}
