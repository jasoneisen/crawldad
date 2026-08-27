using Crawldad.Api.Features.Runs;
using Crawldad.Api.Features.Runs.Interpreter.Expressions;
using Crawldad.Api.Infrastructure.Storage;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Unit;

/// <summary>Strict extraction end-to-end through the interpreter on the record/replay fake (issue #75): a selector miss
/// is countable in <c>stats.selectorMisses</c> and emits a <c>SelectorMiss</c> event (soft mode, run still succeeds),
/// <c>require(...)</c> / <c>config.strictExtraction</c> make a miss a terminal <c>selector_miss</c>, and the required
/// form composes with <c>captureOnFailure</c>. Both backends behave identically — the parity is asserted for real
/// Chromium in <see cref="Integration.RealChromiumStrictExtractionTests"/>.</summary>
public class StrictExtractionTests
{
    // The capture-sample fixture: a parcel-detail page (#content h1 = "Parcel 42", .owner) at a fixed goto URL. #recordNumber
    // and #missing never exist, so they DRIFT (miss); the real selectors match, so the run shapes normally.
    private const string _inputs = """{ "backend": { "adapter": "fake", "options": { "fixture": "capture-sample" } } }""";

    private const string _goto = """{ "goto": { "url": "https://fixture.test/parcel/42" } }""";

    private static string Payload(string config, string steps, string result = "'ok'") =>
        $$"""
        { "name": "t", "config": { "backend": "input.backend"{{config}} }, "vars": { "items": [1, 2, 3] },
          "steps": [ {{_goto}}, {{steps}} ], "result": "{{result}}" }
        """;

    // ----- soft mode (always on): countable + observable, run still succeeds -----

    [Fact]
    public async Task A_soft_miss_is_counted_in_stats_while_the_run_succeeds()
    {
        // The documented drift idiom coalesce(text(...), '') hides the null — but the miss is now visible in stats,
        // alongside a real extraction that matched. This is the #47 drift signal: "succeeded but selectorMisses > 0".
        var outcome = await Runner.RunAsync(
            Payload(
                "",
                """{ "set": { "var": "rec", "value": "coalesce(text('#recordNumber'), '')" } }, { "set": { "var": "owner", "value": "text('.owner')" } }""",
                "{ rec: rec, owner: owner }"),
            _inputs);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        outcome.Result!.Value.GetProperty("rec").GetString().ShouldBe("");                 // the drifted field degraded to ""
        outcome.Result.Value.GetProperty("owner").GetString().ShouldBe("Owner: Jane Q. Public"); // the healthy field
        outcome.Stats.SelectorMisses.ShouldBe(1);
    }

    [Fact]
    public async Task A_matched_but_empty_element_is_not_counted_as_a_miss()
    {
        // The selector-variants #startDate is <input value=""> — its textContent is "", a matched-but-blank element,
        // which must NOT read as drift (the distinction coalesce erases today).
        const string VariantInputs = """{ "backend": { "adapter": "fake", "options": { "fixture": "selector-variants" } } }""";
        const string Body =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": {},
              "steps": [ { "goto": { "url": "https://variants.test/page" } },
                         { "set": { "var": "d", "value": "text('#startDate')" } } ],
              "result": "d" }
            """;

        var outcome = await Runner.RunAsync(Body, VariantInputs);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        outcome.Result!.Value.GetString().ShouldBe("");   // matched, blank
        outcome.Stats.SelectorMisses.ShouldBe(0);         // …but not a miss
    }

    [Fact]
    public async Task The_durable_path_emits_a_selector_miss_event_naming_the_selector_and_step()
    {
        var (outcome, observer, _) = await Runner.RunWithObserverAsync(
            Payload("", """{ "set": { "var": "x", "value": "coalesce(text('#recordNumber'), '')" } }"""), _inputs);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        var miss = observer.Events.OfType<SelectorMiss>().ShouldHaveSingleItem();
        miss.Selector.ShouldBe("#recordNumber");
        miss.StepIndex.ShouldBe(1); // step 0 is the goto; the missing extraction is step 1
    }

    [Fact]
    public async Task Repeated_misses_of_one_selector_count_each_but_emit_a_single_deduped_event()
    {
        // A per-row extraction that drifts misses on every iteration: the counter reflects the true magnitude (3), but
        // the event stream carries ONE SelectorMiss for the selector — dedupe keeps a fleet-scale loop from flooding it.
        var (outcome, observer, _) = await Runner.RunWithObserverAsync(
            Payload("", """{ "forEach": { "in": "items", "as": "it", "maxIterations": 10, "do": [ { "set": { "var": "x", "value": "coalesce(text('#missing'), '')" } } ] } }"""),
            _inputs);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        outcome.Stats.SelectorMisses.ShouldBe(3);                          // counted every time
        observer.Events.OfType<SelectorMiss>().ShouldHaveSingleItem();     // emitted once (deduped by selector)
    }

    [Fact]
    public async Task Soft_misses_are_counted_on_the_synchronous_path_too()
    {
        // No observer ⇒ no SelectorMiss event, but the counter still rides the returned Stats (both run shapes report it).
        var outcome = await Runner.RunWithFakeAsync(
            Payload("", """{ "set": { "var": "x", "value": "coalesce(text('#missing'), '')" } }"""), _inputs);

        outcome.Outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Outcome.Failure?.Code);
        outcome.Outcome.Stats.SelectorMisses.ShouldBe(1);
        outcome.Outcome.Events.OfType<SelectorMiss>().ShouldBeEmpty();
    }

    // ----- required extraction: a miss is terminal selector_miss -----

    [Fact]
    public async Task A_required_extraction_fails_the_run_with_a_classified_selector_miss()
    {
        var outcome = await Runner.RunAsync(
            Payload("", """{ "set": { "var": "recordNumber", "value": "require(text('#recordNumber'))" } }"""), _inputs);

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Class.ShouldBe("terminal");
        outcome.Failure.Code.ShouldBe("selector_miss");
        outcome.Failure.Message.ShouldContain("#recordNumber");
        outcome.Failure.AtStep.Index.ShouldBe(1);
        outcome.Stats.SelectorMisses.ShouldBe(1); // the terminal miss is still counted
    }

    // ----- config.strictExtraction: ANY miss is terminal -----

    [Fact]
    public async Task Strict_extraction_makes_an_unrequired_miss_terminal()
    {
        var outcome = await Runner.RunAsync(
            Payload(""", "strictExtraction": true""", """{ "set": { "var": "x", "value": "text('#missing')" } }"""), _inputs);

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Class.ShouldBe("terminal");
        outcome.Failure.Code.ShouldBe("selector_miss");
    }

    [Fact]
    public async Task Strict_extraction_off_by_default_keeps_a_miss_soft()
    {
        var outcome = await Runner.RunAsync(
            Payload("", """{ "set": { "var": "x", "value": "text('#missing')" } }"""), _inputs);

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code); // default is soft
        outcome.Stats.SelectorMisses.ShouldBe(1);
    }

    // ----- composition with captureOnFailure (#73/#78): the acceptance flow -----

    [Fact]
    public async Task A_required_miss_composes_with_captureOnFailure_banking_the_drifted_page()
    {
        // The acceptance sketch: a required recordNumber extraction fails selector_miss when the id drifts, and the
        // failing page's HTML is banked to BYO storage next to the failure screenshot — the clearest drift diagnostic.
        var sink = new FakeDownloadSink();
        var (outcome, observer, _) = await Runner.RunWithObserverAsync(
            Payload(""", "captureOnFailure": { "to": "{ kind: 'fake', name: 'x' }" }""",
                """{ "set": { "var": "recordNumber", "value": "require(text('#ctl00_lblRecordNumber'))" } }"""),
            _inputs,
            sink: sink);

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe("selector_miss");

        // The soft signal fired first (naming the drifted selector), then the terminal failure.
        observer.Events.OfType<SelectorMiss>().ShouldHaveSingleItem().Selector.ShouldBe("#ctl00_lblRecordNumber");

        // The failing page's HTML was captured (contains the fixture body), and StepFailed still carries the screenshot ref.
        var captured = observer.Events.OfType<Captured>().ShouldHaveSingleItem();
        System.Text.Encoding.UTF8.GetString(sink.BytesOf(sink.Stored.Single())).ShouldContain("Parcel 42");
        captured.BlobRef.ShouldEndWith(".html");
        observer.Events.OfType<StepFailed>().ShouldHaveSingleItem().ScreenshotRef.ShouldNotBeNull();
    }

    // ----- scope-only paths (no interpreter): the inert sink -----

    [Fact]
    public async Task A_run_scope_with_no_sink_uses_the_inert_no_op_that_still_honours_require()
    {
        // A bare RunScope (the scope/selector unit paths, no interpreter behind it) falls back to NoSelectorMissSink:
        // a soft miss is a silent null (nothing to count), but require() still raises the terminal selector_miss.
        var scope = await Runner.ScopeOnFakeAsync();

        (await CrawldadExpression.Parse("text('#definitely-not-here')").EvaluateAsync(scope)).ShouldBeNull();

        var error = await Should.ThrowAsync<ExpressionEvaluationException>(
            async () => await CrawldadExpression.Parse("require(text('#definitely-not-here'))").EvaluateAsync(scope));
        error.Code.ShouldBe(ExpressionErrorCodes.SelectorMiss);
    }
}
