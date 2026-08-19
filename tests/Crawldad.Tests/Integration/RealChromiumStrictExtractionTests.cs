using System.Text.Json;
using System.Text.Json.Nodes;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Integration;

/// <summary>Drives strict extraction (issue #75) through the executor saga against real headless Chromium (the parity
/// <c>local</c> backend, fixture corpus only), proving the miss-vs-empty semantics are identical to the fake: real
/// Playwright returns null for a zero-match selector (a miss) and a real string — possibly empty — for a matched element
/// (not a miss). A soft miss is counted in <c>stats.selectorMisses</c> while the run succeeds; a <c>require(...)</c> miss
/// fails the run <c>selector_miss</c>. The AngleSharp equivalents are <see cref="Unit.StrictExtractionTests"/>.</summary>
[Collection(RealChromiumCollection.Name)]
public class RealChromiumStrictExtractionTests(ParityAppFixture fixture)
{
    // The form page (#ctl00_PlaceHolderMain_btnNewSearch = "Search") the local backend renders for caphome-search; the
    // lblRecordNumber id is absent, so it DRIFTS — the parity target for real Playwright's null-on-no-match.
    private const string _goto =
        """{ "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } }, { "waitForLoadState": { "state": "load" } }""";

    private static JsonObject Body(string config, string steps, string result) => new()
    {
        ["payload"] = JsonNode.Parse(
            $$"""
            { "crawldad": "1", "name": "strict.parity", "config": { "backend": "input.backend"{{config}} }, "vars": {},
              "steps": [ {{_goto}}, {{steps}} ], "result": "{{result}}" }
            """),
        ["inputs"] = new JsonObject { ["backend"] = new JsonObject { ["adapter"] = "local", ["options"] = new JsonObject { ["fixture"] = "caphome-search" } } },
        ["async"] = true,
    };

    private async Task<JsonElement> RunAsync(JsonObject body)
    {
        var host = fixture.Host;
        var accepted = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBe(202);
        });
        var runId = (await accepted.ReadAsJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        return await DurableHost.PollUntilTerminalAsync(host, runId, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_zero_match_selector_is_a_soft_miss_while_a_matched_element_is_not()
    {
        // Real Chromium: the absent id reads null (a miss, counted), while the present <a> reads its real text (matched,
        // never a miss) — the exact distinction the fake models, on a genuine rendered DOM.
        var terminal = await RunAsync(Body(
            "",
            """{ "set": { "var": "label", "value": "text('#ctl00_PlaceHolderMain_btnNewSearch')" } }, { "set": { "var": "rec", "value": "coalesce(text('#ctl00_PlaceHolderMain_lblRecordNumber'), '')" } }""",
            "{ label: label, rec: rec }"));

        terminal.GetProperty("status").GetString().ShouldBe("succeeded", terminal.ToString());
        terminal.GetProperty("result").GetProperty("label").GetString().ShouldBe("Search"); // matched — not a miss
        terminal.GetProperty("result").GetProperty("rec").GetString().ShouldBe("");          // drifted — degraded to ""
        terminal.GetProperty("stats").GetProperty("selectorMisses").GetInt32().ShouldBe(1);  // …and counted as one miss
    }

    [Fact]
    public async Task A_required_extraction_fails_selector_miss_when_the_id_drifts()
    {
        var terminal = await RunAsync(Body(
            "",
            """{ "set": { "var": "rec", "value": "require(text('#ctl00_PlaceHolderMain_lblRecordNumber'))" } }""",
            "rec"));

        terminal.GetProperty("status").GetString().ShouldBe("failed", terminal.ToString());
        terminal.GetProperty("failure").GetProperty("class").GetString().ShouldBe("terminal");
        terminal.GetProperty("failure").GetProperty("code").GetString().ShouldBe("selector_miss");
    }
}
