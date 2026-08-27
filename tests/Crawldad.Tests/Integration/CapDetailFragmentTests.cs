using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Api.Features.Runs;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>Drives <c>POST /runs</c> against the fake backend for each of LJCMG's four hardest CapDetail fragments
/// and asserts the shaped <c>result</c> is byte-identical to a hand-derived golden, plus the exact warning/error
/// trace. Covers the address <c>&lt;br&gt;</c> branch, the violations ladder, processing-status chains, and related-record indentation.</summary>
[Collection(IntegrationCollection.Name)]
public class CapDetailFragmentTests(AppFixture fixture)
{
    private IAlbaHost Host => fixture.Host;

    private static string Link(string cap) =>
        $"https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?Module=Enforcement&capID={cap}&agencyCode=LJCMG";

    [Fact]
    public async Task Address_fragment_is_byte_identical_to_golden()
    {
        var (result, logs) = await RunFragmentAsync("address", "24ENF-00001");

        AssertMatchesGolden("capdetail-address", result);
        logs.ShouldBe([("warning", $"Exceptional location address lines (2): {Link("24ENF-00001")}")]);
    }

    [Fact]
    public async Task Violations_fragment_is_byte_identical_to_golden()
    {
        var (result, logs) = await RunFragmentAsync("violations", "24ENF-00002");

        AssertMatchesGolden("capdetail-violations", result);
        logs.ShouldBe([("warning", "Unknown heading in application information table")]);
    }

    [Fact]
    public async Task Processing_fragment_is_byte_identical_to_golden()
    {
        var (result, logs) = await RunFragmentAsync("processing", "24ENF-00003");

        AssertMatchesGolden("capdetail-processing", result);
        logs.ShouldBe([("warning", $"Could not parse additional comment lines: {Link("24ENF-00003")}")]);
    }

    [Fact]
    public async Task Related_fragment_is_byte_identical_to_golden()
    {
        var (result, logs) = await RunFragmentAsync("related", "24ENF-00004");

        AssertMatchesGolden("capdetail-related", result);

        // The highlighted row resolves to a NON-TRIVIAL ancestor and REC-D resolves to REC-C (not REC-A), proving the
        // multi-level "greatest indent strictly less than current" walk over the mutating parents map.
        result.GetProperty("parentRecordNumber").GetString().ShouldBe("REC-A");
        var records = result.GetProperty("relatedRecords");
        records[3].GetProperty("recordNumber").GetString().ShouldBe("REC-D");
        records[3].GetProperty("parentRecordNumber").GetString().ShouldBe("REC-C");

        // The garbled-width row (error, indent-0 fallback) and the unknown-class row (error, not added) each log once.
        logs.ShouldBe([
            ("error", $"Could not determine indentation of related record: {Link("24ENF-00004")}"),
            ("error", $"Could not determine class of related record: {Link("24ENF-00004")}"),
        ]);
    }

    // ----- helpers -----------------------------------------------------------

    private async Task<(JsonElement Result, List<(string Level, string Message)> Logs)> RunFragmentAsync(string name, string cap)
    {
        var payload = await File.ReadAllTextAsync(Path.Combine(Runner.FixturesRoot, "Payloads", $"{name}-fragment.json"));
        var body = new JsonObject
        {
            ["payload"] = JsonNode.Parse(payload),
            ["inputs"] = new JsonObject
            {
                ["backend"] = new JsonObject
                {
                    ["adapter"] = "fake",
                    ["options"] = new JsonObject { ["fixture"] = $"capdetail-{name}" },
                },
                ["link"] = Link(cap),
            },
        };

        var scenario = await Host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBeOk();
        });
        var root = await scenario.ReadAsJsonAsync<JsonElement>();

        root.GetProperty("status").GetString().ShouldBe("succeeded");
        var result = root.GetProperty("result").Clone();

        var runId = root.GetProperty("runId").GetGuid();
        var store = Host.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(TestTenants.PrimaryId);
        var events = await session.Events.FetchStreamAsync(runId);
        var logs = events.Select(e => e.Data).OfType<LogEmitted>().Select(l => (l.Level, l.Message)).ToList();

        return (result, logs);
    }

    private static void AssertMatchesGolden(string fixtureDir, JsonElement result)
    {
        using var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(Runner.FixturesRoot, fixtureDir, "golden.json")));
        JsonAssert.DeepEquals(result, golden.RootElement).ShouldBeTrue();                    // structural
        JsonAssert.Canonical(result).ShouldBe(JsonAssert.Canonical(golden.RootElement));     // byte compare
    }
}
