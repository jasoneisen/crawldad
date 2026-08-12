using System.Text;
using System.Text.Json;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Fixtures;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Browser.Fake;

namespace Crawldad.Tests.Unit;

/// <summary>White-box tests for the record-mode engine: what a recorded session banks into a manifest, its dedup and
/// caps, and the operations it refuses to record. The HTTP end-to-end record→replay→golden path is exercised by
/// <c>FixtureRecordReplayTests</c>; these pin the recorder's own branches without a database.</summary>
public class FixtureRecorderTests
{
    // The fake serving the search+detail "site" the record run drives.
    private const string _siteInputs = """{ "backend": { "adapter": "fake", "options": { "fixture": "record-search-detail" } } }""";

    private static string SearchDetailPayload() =>
        File.ReadAllText(Path.Combine(Runner.FixturesRoot, "Payloads", "record-search-detail.json"));

    [Fact]
    public async Task Records_the_search_detail_state_machine()
    {
        var recorder = new FixtureRecorder();
        var outcome = await Runner.RunWithRecorderAsync(SearchDetailPayload(), _siteInputs, recorder);
        outcome.Status.ShouldBe(RunStatus.Succeeded);

        var recorded = recorder.Build();
        recorded.PageCount.ShouldBe(3);        // form, results, detail
        recorded.TransitionCount.ShouldBe(2);  // search click, detail click
        recorded.Pages.Count.ShouldBe(3);
        recorded.TotalBytes.ShouldBeGreaterThan(0);

        using var manifest = JsonDocument.Parse(recorded.ManifestJson);
        var root = manifest.RootElement;
        root.GetProperty("manifest").GetString().ShouldBe("1");

        // The initial state carries the goto URL; every state's html is a content hash present in the page map.
        var initial = root.GetProperty("initialState").GetString()!;
        var states = root.GetProperty("states");
        states.GetProperty(initial).GetProperty("gotoUrl").GetString().ShouldBe("https://county.example/search");
        foreach (var state in states.EnumerateObject())
        {
            recorded.Pages.ContainsKey(state.Value.GetProperty("html").GetString()!).ShouldBeTrue();
        }

        // Two transitions in interaction order; the first (the search postback) carries the recorded emit.
        var transitions = root.GetProperty("transitions");
        transitions.GetArrayLength().ShouldBe(2);
        var search = transitions[0];
        search.GetProperty("on").GetProperty("click").GetString().ShouldBe("#searchBtn");
        search.GetProperty("emit").GetProperty("method").GetString().ShouldBe("POST");
        transitions[1].GetProperty("on").GetProperty("click").GetString().ShouldBe("#detailLink");
    }

    [Fact]
    public async Task A_repeat_navigation_dedupes_and_keeps_one_initial_state()
    {
        // Two gotos to the same URL: the second dedupes to the same content-addressed state and leaves the initial state
        // and its gotoUrl unchanged (the recorder fixes both on first sight).
        const string payload = """
        { "crawldad": "1", "name": "repeat", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [
            { "goto": { "url": "https://county.example/search" } },
            { "goto": { "url": "https://county.example/search" } }
          ],
          "result": "'ok'" }
        """;

        var recorder = new FixtureRecorder();
        (await Runner.RunWithRecorderAsync(payload, _siteInputs, recorder)).Status.ShouldBe(RunStatus.Succeeded);

        var recorded = recorder.Build();
        recorded.PageCount.ShouldBe(1);
        recorded.TransitionCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_wait_for_request_without_a_method_records_a_default_get_emit()
    {
        // A waitForRequest with no method still records an emit (default GET) so a strict replay's postback wait matches.
        const string payload = """
        { "crawldad": "1", "name": "nomethod", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [
            { "goto": { "url": "https://county.example/search" } },
            { "waitForRequest": { "urlPrefix": "https://county.example/search",
                "trigger": [ { "click": { "selector": "#searchBtn" } } ] } }
          ],
          "result": "'ok'" }
        """;

        var recorder = new FixtureRecorder();
        (await Runner.RunWithRecorderAsync(payload, _siteInputs, recorder)).Status.ShouldBe(RunStatus.Succeeded);

        using var manifest = JsonDocument.Parse(recorder.Build().ManifestJson);
        manifest.RootElement.GetProperty("transitions")[0].GetProperty("emit").GetProperty("method").GetString().ShouldBe("GET");
    }

    [Fact]
    public async Task A_session_over_the_page_cap_is_unrecordable()
    {
        var outcome = await Runner.RunWithRecorderAsync(SearchDetailPayload(), _siteInputs, new FixtureRecorder(maxStates: 1));
        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe(FixtureRecorder.UnrecordableCode);
    }

    [Fact]
    public async Task A_session_over_the_byte_cap_is_unrecordable()
    {
        var outcome = await Runner.RunWithRecorderAsync(SearchDetailPayload(), _siteInputs, new FixtureRecorder(maxBytes: 1));
        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe(FixtureRecorder.UnrecordableCode);
    }

    [Fact]
    public async Task A_structured_click_selector_is_unrecordable()
    {
        const string payload = """
        { "crawldad": "1", "name": "structured", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [
            { "goto": { "url": "https://county.example/search" } },
            { "click": { "selector": { "title": "Run search" } } }
          ],
          "result": "'ok'" }
        """;

        var outcome = await Runner.RunWithRecorderAsync(payload, _siteInputs, new FixtureRecorder());
        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe(FixtureRecorder.UnrecordableCode);
    }

    [Fact]
    public async Task An_in_frame_click_is_unrecordable()
    {
        const string payload = """
        { "crawldad": "1", "name": "inframe", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [
            { "goto": { "url": "https://county.example/search" } },
            { "frame": { "selector": "#anyframe", "var": "f" } },
            { "click": { "selector": "#searchBtn", "in": "f" } }
          ],
          "result": "'ok'" }
        """;

        var outcome = await Runner.RunWithRecorderAsync(payload, _siteInputs, new FixtureRecorder());
        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe(FixtureRecorder.UnrecordableCode);
    }

    [Fact]
    public async Task A_download_is_unrecordable()
    {
        const string payload = """
        { "crawldad": "1", "name": "dl", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [
            { "goto": { "url": "https://county.example/search" } },
            { "download": { "to": "{ kind: 'fake', name: 's' }", "var": "dl",
                "trigger": [ { "click": { "selector": "#searchBtn" } } ] } }
          ],
          "result": "'ok'" }
        """;

        var outcome = await Runner.RunWithRecorderAsync(payload, _siteInputs, new FixtureRecorder());
        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe(FixtureRecorder.UnrecordableCode);
    }

    [Fact]
    public async Task A_session_that_never_navigates_is_unrecordable_at_build()
    {
        const string payload = """
        { "crawldad": "1", "name": "nogoto", "inputs": { "backend": { "type": "backend", "required": true } },
          "config": { "backend": "input.backend" },
          "steps": [ { "comment": "no navigation" } ],
          "result": "'ok'" }
        """;

        var recorder = new FixtureRecorder();
        (await Runner.RunWithRecorderAsync(payload, _siteInputs, recorder)).Status.ShouldBe(RunStatus.Succeeded);

        var ex = Should.Throw<CrawldadFailureException>(recorder.Build);
        ex.Code.ShouldBe(FixtureRecorder.UnrecordableCode);
    }

    [Fact]
    public void Fixture_name_rules_reject_null_and_bad_slugs_and_accept_good_ones()
    {
        FixtureNameRules.IsValidName(null).ShouldBeFalse();
        FixtureNameRules.IsValidName("Bad_Name").ShouldBeFalse();
        FixtureNameRules.IsValidName("accela-search").ShouldBeTrue();
    }

    [Fact]
    public void In_memory_fixture_content_reads_text_and_utf8_bytes()
    {
        var content = new InMemoryFixtureContent(new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "héllo" });
        content.ReadText("k").ShouldBe("héllo");
        content.ReadBytes("k").ShouldBe(Encoding.UTF8.GetBytes("héllo"));
    }
}
