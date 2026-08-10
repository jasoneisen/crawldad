using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;

namespace Crawldad.Tests.Unit;

/// <summary>The interpreter's semantic step-trace emission: on the durable path (an observer is present) it emits one event
/// per meaningful action — session-opened, step markers, navigations, clicks, waits, extracted-value refs, downloads — and
/// captures a screenshot on failure; the synchronous path (no observer) emits none. Driven via <see cref="Runner"/> against the fake.</summary>
public class RunTraceEmissionTests
{
    private static IEnumerable<T> OfType<T>(RecordingObserver observer) => observer.Events.OfType<T>();

    [Fact]
    public async Task The_durable_path_emits_session_step_navigation_click_wait_and_extract_events()
    {
        var (outcome, observer, _) = await Runner.RunWithObserverAsync(Runner.FragmentPayload());
        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);

        // Session-opened carries the backend region (the fake reports "fake") — the RunTimeline's region source.
        OfType<RunSessionOpened>(observer).Single().Region.ShouldBe("fake");

        // One StepStarted per top-level step, the first being the goto at index 0.
        var steps = OfType<StepStarted>(observer).ToList();
        steps.ShouldNotBeEmpty();
        steps[0].Index.ShouldBe(0);
        steps[0].Kind.ShouldBe("goto");

        OfType<Navigated>(observer).ShouldHaveSingleItem().Url.ShouldContain("CapHome.aspx"); // the goto
        OfType<Clicked>(observer).ShouldNotBeEmpty(); // the search button inside the waitForRequest trigger

        // Waits record what was awaited: the load state, the postback request, and the overlay selector.
        var waitKinds = OfType<Waited>(observer).Select(w => w.Kind).ToList();
        waitKinds.ShouldContain("loadState:networkidle");
        waitKinds.ShouldContain("request");
        waitKinds.ShouldContain("selector:hidden");

        // Extracted carries the target key + a shape ref (never the value): the loop's pushes into pageResults + the set.
        var extractKeys = OfType<Extracted>(observer).Select(e => e.Key).ToList();
        extractKeys.ShouldContain("pageResults");
        extractKeys.ShouldContain("hasMorePages");
        OfType<Extracted>(observer).ShouldAllBe(e => !e.ValueRef.Contains("aca-prod", StringComparison.Ordinal)); // metadata only
    }

    [Fact]
    public async Task The_synchronous_path_emits_no_step_trace_events()
    {
        // No observer ⇒ StepAsync no-ops; only the coarse LogEmitted/RunAttemptFailed accrue for the endpoint to append.
        var (outcome, _) = await Runner.RunWithFakeAsync(Runner.FragmentPayload());
        outcome.Status.ShouldBe(RunStatus.Succeeded);

        outcome.Events.Any(e => e is StepStarted or Navigated or Clicked or Waited or Extracted or RunSessionOpened).ShouldBeFalse();
    }

    [Theory]
    [InlineData("input.nope", "null")] // absent input key resolves to null
    [InlineData("'hi'", "string(2)")]
    [InlineData("[1, 2, 3]", "list(3)")]
    [InlineData("{ a: 1, b: 2 }", "map(2)")]
    [InlineData("true", "scalar")]
    [InlineData("5", "scalar")]
    public async Task Extracted_records_a_pii_safe_shape_ref_for_each_value_kind(string valueExpr, string expectedRef)
    {
        var payload = $$"""{ "name": "t", "config": { "backend": "input.backend" }, "vars": {}, "steps": [ { "set": { "var": "x", "value": "{{valueExpr}}" } } ], "result": "'ok'" }""";
        var (outcome, observer, _) = await Runner.RunWithObserverAsync(payload);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        var extracted = OfType<Extracted>(observer).ShouldHaveSingleItem();
        extracted.Key.ShouldBe("x");
        extracted.ValueRef.ShouldBe(expectedRef);
    }

    [Fact]
    public async Task Set_with_a_path_emits_an_extracted_event()
    {
        // The set-with-path form mutates inside a map var; it still records an Extracted for the target var.
        const string Payload =
            """{ "name": "t", "config": { "backend": "input.backend" }, "vars": { "m": {} }, "steps": [ { "set": { "var": "m", "path": "k", "value": "'v'" } } ], "result": "m.k" }""";
        var (outcome, observer, _) = await Runner.RunWithObserverAsync(Payload);

        outcome.Result!.Value.GetString().ShouldBe("v");
        OfType<Extracted>(observer).ShouldContain(e => e.Key == "m" && e.ValueRef == "string(1)");
    }

    [Fact]
    public async Task A_download_emits_a_downloaded_event_with_a_guessed_content_type()
    {
        const string DownloadInputs =
            """{ "backend": { "adapter": "fake", "options": { "fixture": "download-sample" } }, "attachmentStore": { "kind": "fake", "name": "s" } }""";
        var payload = await File.ReadAllTextAsync(Path.Combine(Runner.FixturesRoot, "Payloads", "download-fragment.json"));
        var (outcome, observer, _) = await Runner.RunWithObserverAsync(payload, DownloadInputs);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        var download = OfType<Downloaded>(observer).First();
        download.BlobRef.ShouldEndWith(".pdf");           // the engine's stored name (from the suggested report.pdf)
        download.ContentType.ShouldBe("application/pdf"); // guessed from the .pdf extension
        download.Size.ShouldBe(30);
        download.Sha256.Length.ShouldBe(64);
    }

    [Fact]
    public async Task A_log_node_and_a_retry_attempt_emit_through_the_observer_on_the_durable_path()
    {
        // A payload that logs then retries (the inject-timeout fixture fails the #go click twice, then succeeds) proves
        // LogEmitted + RunAttemptFailed flow live through the observer (in occurrence order), not buffered for the endpoint.
        const string Inputs = """{ "backend": { "adapter": "fake", "options": { "fixture": "inject-timeout" } } }""";
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend", "retry": { "maxAttempts": 3, "delayMs": 0, "retryOn": ["timeout"] } }, "vars": {},
              "steps": [
                { "goto": { "url": "https://fixture.test/form" } },
                { "log": { "level": "info", "message": "searching" } },
                { "click": { "selector": "#go" } }
              ],
              "result": "'ok'" }
            """;
        var (outcome, observer, _) = await Runner.RunWithObserverAsync(Payload, Inputs);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        OfType<LogEmitted>(observer).ShouldContain(l => l.Message == "searching");
        OfType<RunAttemptFailed>(observer).ShouldContain(a => a.Code == "timeout"); // the scripted timeout, retried
    }
}
