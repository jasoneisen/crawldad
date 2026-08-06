using System.Text.Json;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Features.Runs.Interpreter;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The Phase 2 control/effect nodes (§6): <c>switch</c>, <c>guard</c>, <c>fail</c>, <c>log</c>, the do-while
/// <c>loop { while }</c>, <c>onMaxIterations</c> on every loop form, <c>set</c> with a <c>path</c>, and template
/// interpolation reaching selectors. Each case runs a small payload against the fake backend.
/// </summary>
public class InterpreterNodesTests
{
    private static string Payload(string steps, string result = "null", string vars = "{}") =>
        $$"""{ "name": "t", "config": { "backend": "input.backend" }, "vars": {{vars}}, "steps": {{steps}}, "result": "{{result}}" }""";

    private static Task<RunOutcome> Run(string steps, string result = "null", string vars = "{}", string inputs = Runner.FakeInputs) =>
        Runner.RunAsync(Payload(steps, result, vars), inputs);

    private static JsonElement Ok(RunOutcome outcome)
    {
        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        return outcome.Result!.Value;
    }

    private static RunFailureDetail Fail(RunOutcome outcome)
    {
        outcome.Status.ShouldBe(RunStatus.Failed);
        return outcome.Failure!;
    }

    // ----- switch ------------------------------------------------------------

    [Fact]
    public async Task Switch_takes_the_first_true_case()
    {
        Ok(await Run(
            """
            [ { "switch": { "cases": [
                { "when": "false", "do": [ { "set": { "var": "x", "value": "1" } } ] },
                { "when": "true",  "do": [ { "set": { "var": "x", "value": "2" } } ] },
                { "when": "true",  "do": [ { "set": { "var": "x", "value": "3" } } ] }
              ] } } ]
            """,
            result: "x")).GetInt64().ShouldBe(2);
    }

    [Fact]
    public async Task Switch_falls_to_default_when_no_case_matches()
    {
        Ok(await Run(
            """[ { "switch": { "cases": [ { "when": "false", "do": [ { "set": { "var": "x", "value": "1" } } ] } ], "default": [ { "set": { "var": "x", "value": "9" } } ] } } ]""",
            result: "x")).GetInt64().ShouldBe(9);
    }

    [Fact]
    public async Task Switch_with_no_match_and_no_default_is_a_no_op()
    {
        Ok(await Run(
            """[ { "switch": { "cases": [ { "when": "false", "do": [ { "set": { "var": "x", "value": "1" } } ] } ] } } ]""",
            result: "x", vars: """{ "x": 0 }""")).GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task Switch_case_break_propagates_to_the_enclosing_loop()
    {
        Ok(await Run(
            """
            [ { "loop": { "maxIterations": 100, "for": { "var": "i", "from": "0", "to": "5" }, "do": [
                { "switch": { "cases": [ { "when": "i == 2", "do": [ { "break": {} } ] } ] } },
                { "push": { "into": "acc", "value": "i" } }
              ] } } ]
            """,
            result: "acc", vars: """{ "acc": [] }"""))
            .EnumerateArray().Select(e => e.GetInt64()).ShouldBe([0L, 1L]);
    }

    // ----- guard / fail ------------------------------------------------------

    [Fact]
    public async Task Guard_passes_when_the_condition_holds_and_fails_otherwise()
    {
        Ok(await Run("""[ { "guard": { "cond": "true", "elseFail": { "class": "terminal", "code": "x", "message": "no" } } } ]""", result: "'ok'"))
            .GetString().ShouldBe("ok");

        var failure = Fail(await Run(
            """[ { "guard": { "cond": "1 > 2", "elseFail": { "class": "terminal", "code": "record_not_accessible", "message": "redirected to ${1 + 1}" } } } ]"""));
        failure.Class.ShouldBe("terminal");
        failure.Code.ShouldBe("record_not_accessible");
        failure.Message.ShouldBe("redirected to 2");
    }

    [Fact]
    public async Task Fail_raises_unconditionally_and_renders_its_message_template()
    {
        var failure = Fail(await Run(
            """[ { "fail": { "class": "terminal", "code": "unknown_heading", "message": "UNKNOWN HEADING: ${upper('owner')}" } } ]"""));
        failure.Class.ShouldBe("terminal");
        failure.Code.ShouldBe("unknown_heading");
        failure.Message.ShouldBe("UNKNOWN HEADING: OWNER");
    }

    [Fact]
    public async Task Fail_class_retryable_with_no_retry_config_exhausts_immediately()
    {
        // A retryable fail participates in retry, but with the default single attempt it exhausts at once.
        var failure = Fail(await Run(
            """[ { "fail": { "class": "retryable", "code": "flaky", "message": "try again" } } ]"""));
        failure.Class.ShouldBe("retryable-exhausted");
        failure.Code.ShouldBe("flaky");
    }

    // ----- log ---------------------------------------------------------------

    [Fact]
    public async Task Log_appends_events_at_each_level_and_never_fails_the_run()
    {
        var outcome = await Run(
            """
            [ { "log": { "level": "info",    "message": "starting" } },
              { "log": { "level": "warning", "message": "watch out ${1 + 1}" } },
              { "log": { "level": "error",   "message": "still not a failure" } } ]
            """,
            result: "'ok'");

        Ok(outcome).GetString().ShouldBe("ok");
        var logs = outcome.Events.OfType<LogEmitted>().ToList();
        logs.Select(l => l.Level).ShouldBe(["info", "warning", "error"]);
        logs[1].Message.ShouldBe("watch out 2");
        logs[0].At.ShouldBe(FakeClock.Fixed);
    }

    // ----- loop { while } (do-while) ----------------------------------------

    [Fact]
    public async Task While_loop_runs_the_body_before_testing()
    {
        // `while` is false from the start, yet the body runs once (do-while), so n becomes 1.
        Ok(await Run(
            """[ { "loop": { "maxIterations": 100, "while": "n < 0", "do": [ { "set": { "var": "n", "value": "n + 1" } } ] } } ]""",
            result: "n", vars: """{ "n": 0 }""")).GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task While_loop_iterates_until_the_condition_is_false()
    {
        Ok(await Run(
            """[ { "loop": { "maxIterations": 100, "while": "n < 3", "do": [ { "set": { "var": "n", "value": "n + 1" } } ] } } ]""",
            result: "n", vars: """{ "n": 0 }""")).GetInt64().ShouldBe(3);
    }

    [Fact]
    public async Task While_loop_break_stops_it()
    {
        Ok(await Run(
            """[ { "loop": { "maxIterations": 100, "while": "true", "do": [ { "set": { "var": "n", "value": "n + 1" } }, { "break": { "when": "n == 2" } } ] } } ]""",
            result: "n", vars: """{ "n": 0 }""")).GetInt64().ShouldBe(2);
    }

    [Fact]
    public async Task While_loop_default_on_max_iterations_is_terminal()
    {
        Fail(await Run(
            """[ { "loop": { "maxIterations": 2, "while": "true", "do": [ { "set": { "var": "n", "value": "n + 1" } } ] } } ]""",
            vars: """{ "n": 0 }"""))
            .Code.ShouldBe(InterpreterErrorCodes.MaxIterationsExceeded);
    }

    // ----- onMaxIterations: "warn" on every loop form ------------------------

    [Fact]
    public async Task While_loop_on_max_iterations_warn_logs_and_exits_normally()
    {
        var outcome = await Run(
            """[ { "loop": { "maxIterations": 2, "onMaxIterations": "warn", "while": "true", "do": [ { "set": { "var": "n", "value": "n + 1" } } ] } } ]""",
            result: "n", vars: """{ "n": 0 }""");

        Ok(outcome).GetInt64().ShouldBe(2); // two bodies ran before the cap stopped it
        outcome.Events.OfType<LogEmitted>().Single().Level.ShouldBe("warning");
    }

    [Fact]
    public async Task For_loop_on_max_iterations_warn_logs_and_exits_normally()
    {
        var outcome = await Run(
            """[ { "loop": { "maxIterations": 2, "onMaxIterations": "warn", "for": { "var": "i", "from": "0", "to": "100" }, "do": [ { "push": { "into": "acc", "value": "i" } } ] } } ]""",
            result: "acc", vars: """{ "acc": [] }""");

        Ok(outcome).EnumerateArray().Select(e => e.GetInt64()).ShouldBe([0L, 1L]);
        outcome.Events.OfType<LogEmitted>().Single().Message.ShouldContain("maxIterations");
    }

    [Fact]
    public async Task ForEach_on_max_iterations_warn_logs_and_exits_normally()
    {
        var outcome = await Run(
            """[ { "forEach": { "in": "['a','b','c','d']", "as": "x", "maxIterations": 2, "onMaxIterations": "warn", "do": [ { "push": { "into": "acc", "value": "x" } } ] } } ]""",
            result: "acc", vars: """{ "acc": [] }""");

        Ok(outcome).EnumerateArray().Select(e => e.GetString()).ShouldBe(["a", "b"]);
        outcome.Events.OfType<LogEmitted>().Single().Level.ShouldBe("warning");
    }

    // ----- set with a path ---------------------------------------------------

    [Fact]
    public async Task Set_path_upserts_a_single_key()
    {
        Ok(await Run(
            """[ { "set": { "var": "v", "path": "title", "value": "'hi'" } } ]""",
            result: "v.title", vars: """{ "v": {} }""")).GetString().ShouldBe("hi");

        // overwrite an existing key
        Ok(await Run(
            """[ { "set": { "var": "v", "path": "title", "value": "'new'" } } ]""",
            result: "v.title", vars: """{ "v": { "title": "old" } }""")).GetString().ShouldBe("new");
    }

    [Fact]
    public async Task Set_path_computed_key_renders_the_template()
    {
        Ok(await Run(
            """[ { "set": { "var": "parents", "path": "[${indent}]", "value": "'R-1'" } } ]""",
            result: "get(parents, '40')", vars: """{ "parents": {}, "indent": 40 }""")).GetString().ShouldBe("R-1");
    }

    [Fact]
    public async Task Set_path_traverses_and_composes_nested_maps()
    {
        Ok(await Run(
            """[ { "set": { "var": "v", "path": "a.b.c", "value": "7" } } ]""",
            result: "v.a.b.c", vars: """{ "v": { "a": { "b": {} } } }""")).GetInt64().ShouldBe(7);

        Ok(await Run(
            """[ { "set": { "var": "v", "path": "a[${k}]", "value": "9" } } ]""",
            result: "get(v.a, 'x')", vars: """{ "v": { "a": {} }, "k": "'x'" }""")).GetInt64().ShouldBe(9);
    }

    [Fact]
    public async Task Set_path_type_errors_on_a_non_map_target_or_untraversable_segment()
    {
        Fail(await Run("""[ { "set": { "var": "v", "path": "title", "value": "1" } } ]""", vars: """{ "v": 5 }"""))
            .Code.ShouldBe("type_error");

        Fail(await Run("""[ { "set": { "var": "missing", "path": "title", "value": "1" } } ]"""))
            .Code.ShouldBe("type_error");

        Fail(await Run("""[ { "set": { "var": "v", "path": "a.b", "value": "1" } } ]""", vars: """{ "v": { "a": 5 } }"""))
            .Code.ShouldBe("type_error");

        // A missing intermediate key cannot be traversed either.
        Fail(await Run("""[ { "set": { "var": "v", "path": "a.b", "value": "1" } } ]""", vars: """{ "v": {} }"""))
            .Code.ShouldBe("type_error");
    }

    // ----- template interpolation reaching selectors -------------------------

    [Fact]
    public async Task Interpolated_selector_resolves_to_a_bound_handle_var_then_to_css()
    {
        // A `${…}`-built string that renders to a bound handle var name wins (interpolate FIRST, then precedence);
        // one that renders to CSS is a page selector.
        var steps = $$"""
            [ {{CapHome.ToResults}}, {{CapHome.LocateRows}},
              { "locate": { "var": "viaVar", "selector": "${'ro' + 'ws'}" } },
              { "locate": { "var": "viaCss", "selector": "#ctl00_PlaceHolderMain_dgvPermitList_gdvPermitList tr:nth-child(${2 + 2})" } } ]
            """;
        Ok(await Run(steps, result: "'' + count(viaVar) + '/' + count(viaCss)")).GetString().ShouldBe("15/1");
    }
}
