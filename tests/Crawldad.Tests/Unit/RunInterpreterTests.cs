using System.Text.Json;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs.Interpreter;

namespace Crawldad.Tests.Unit;

/// <summary>
/// Interpreter v0 node dispatch and control flow beyond the acceptance fragment: if/else, loop bounds + break +
/// caps, forEach over arrays and locators, shadowing, the not-supported-in-v0 guards, stats, and the failure
/// classification (§8.3). Each case runs a small payload against the fake backend.
/// </summary>
public class RunInterpreterTests
{
    private static string Payload(string steps, string result = "null", string vars = "{}", string backendExpr = "input.backend") =>
        $$"""{ "name": "t", "config": { "backend": "{{backendExpr}}" }, "vars": {{vars}}, "steps": {{steps}}, "result": "{{result}}" }""";

    private static Task<RunOutcome> Run(string steps, string result = "null", string vars = "{}", string inputs = Runner.FakeInputs, string backendExpr = "input.backend") =>
        Runner.RunAsync(Payload(steps, result, vars, backendExpr), inputs);

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

    private static List<long> Longs(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetInt64())];

    [Fact]
    public async Task Goto_honours_wait_until_and_a_per_node_timeout()
    {
        var result = Ok(await Run(
            """[ { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement", "waitUntil": "load", "timeoutMs": 5000 } } ]""",
            result: "pageUrl()"));
        result.GetString().ShouldBe("https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement");
    }

    [Fact]
    public async Task If_false_takes_the_else_branch_or_falls_through()
    {
        Ok(await Run("""[ { "if": { "cond": "false", "then": [ { "set": { "var": "x", "value": "1" } } ], "else": [ { "set": { "var": "x", "value": "2" } } ] } } ]""", result: "x"))
            .GetInt64().ShouldBe(2);

        Ok(await Run("""[ { "if": { "cond": "false", "then": [ { "set": { "var": "x", "value": "9" } } ] } } ]""", result: "x", vars: """{ "x": 0 }"""))
            .GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task Vars_string_values_are_expressions_and_vars_may_be_absent()
    {
        Ok(await Run("[]", result: "y", vars: """{ "y": "1 + 1" }""")).GetInt64().ShouldBe(2);

        // A payload with no vars block at all.
        const string NoVars = """{ "name": "t", "config": { "backend": "input.backend" }, "steps": [], "result": "'ok'" }""";
        Ok(await Runner.RunAsync(NoVars)).GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task Loop_inclusive_bound_and_step()
    {
        var result = Ok(await Run(
            """[ { "loop": { "maxIterations": 100, "for": { "var": "i", "from": "0", "to": "4", "inclusiveTo": true, "step": "2" }, "do": [ { "push": { "into": "acc", "value": "i" } } ] } } ]""",
            result: "acc", vars: """{ "acc": [] }"""));
        Longs(result).ShouldBe([0, 2, 4]);
    }

    [Fact]
    public async Task Loop_break_stops_the_loop()
    {
        var result = Ok(await Run(
            """[ { "loop": { "maxIterations": 100, "for": { "var": "i", "from": "0", "to": "10" }, "do": [ { "break": { "when": "i == 3" } }, { "push": { "into": "acc", "value": "i" } } ] } } ]""",
            result: "acc", vars: """{ "acc": [] }"""));
        Longs(result).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task Loop_missing_cap_and_exceeded_cap_are_terminal()
    {
        Fail(await Run("""[ { "loop": { "for": { "var": "i", "from": "0", "to": "1" }, "do": [] } } ]"""))
            .Code.ShouldBe(InterpreterErrorCodes.MissingMaxIterations);

        Fail(await Run("""[ { "loop": { "maxIterations": 2, "for": { "var": "i", "from": "0", "to": "10" }, "do": [] } } ]"""))
            .Code.ShouldBe(InterpreterErrorCodes.MaxIterationsExceeded);
    }

    [Fact]
    public async Task ForEach_over_an_array_with_index_and_continue_and_break()
    {
        Ok(await Run(
            """[ { "forEach": { "in": "['a','b','c']", "as": "item", "index": "idx", "maxIterations": 100, "do": [ { "push": { "into": "acc", "value": "'' + idx + ':' + item" } } ] } } ]""",
            result: "acc", vars: """{ "acc": [] }"""))
            .EnumerateArray().Select(e => e.GetString()).ShouldBe(["0:a", "1:b", "2:c"]);

        Ok(await Run(
            """[ { "forEach": { "in": "['a','skip','c']", "as": "item", "maxIterations": 100, "do": [ { "continue": { "when": "item == 'skip'" } }, { "push": { "into": "acc", "value": "item" } } ] } } ]""",
            result: "acc", vars: """{ "acc": [] }"""))
            .EnumerateArray().Select(e => e.GetString()).ShouldBe(["a", "c"]);

        Ok(await Run(
            """[ { "forEach": { "in": "['a','b','c']", "as": "item", "maxIterations": 100, "do": [ { "break": { "when": "item == 'b'" } }, { "push": { "into": "acc", "value": "item" } } ] } } ]""",
            result: "acc", vars: """{ "acc": [] }"""))
            .EnumerateArray().Select(e => e.GetString()).ShouldBe(["a"]);
    }

    [Fact]
    public async Task ForEach_over_a_bound_locator_iterates_nth_handles()
    {
        var steps = $$"""[ {{CapHome.ToResults}}, {{CapHome.LocateRows}}, { "forEach": { "in": "rows", "as": "r", "maxIterations": 100, "do": [ { "push": { "into": "acc", "value": "count(r)" } } ] } } ]""";
        Ok(await Run(steps, result: "count(acc)", vars: """{ "acc": [] }""")).GetInt64().ShouldBe(15);
    }

    [Fact]
    public async Task ForEach_over_a_non_iterable_is_terminal()
    {
        Fail(await Run("""[ { "forEach": { "in": "5", "as": "x", "maxIterations": 100, "do": [] } } ]"""))
            .Code.ShouldBe(InterpreterErrorCodes.MalformedNode);
    }

    [Fact]
    public async Task ForEach_honours_its_max_iterations_cap()
    {
        Fail(await Run("""[ { "forEach": { "in": "['a','b','c']", "as": "x", "maxIterations": 2, "do": [] } } ]"""))
            .Code.ShouldBe(InterpreterErrorCodes.MaxIterationsExceeded);
    }

    [Fact]
    public async Task Locate_from_a_handle_supports_filter_and_first()
    {
        var steps = $$"""
            [ {{CapHome.ToResults}}, {{CapHome.LocateRows}},
              { "locate": { "var": "voids", "from": "rows", "filter": { "hasTextRegex": "Void" } } },
              { "locate": { "var": "firstVoid", "from": "voids", "first": true } } ]
            """;
        Ok(await Run(steps, result: "count(firstVoid)")).GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task Locate_with_a_base_resolves_a_relative_selector()
    {
        var steps = $$"""
            [ {{CapHome.ToResults}}, {{CapHome.LocateRows}},
              { "locate": { "var": "row3", "from": "rows", "nth": "3" } },
              { "locate": { "var": "cell", "base": "row3", "selector": "td:nth-child(2)" } } ]
            """;
        Ok(await Run(steps, result: "trim(coalesce(text(cell),''))")).GetString().ShouldBe("01/03/2024");
    }

    [Fact]
    public async Task Not_supported_in_v0_guards_frames()
    {
        Fail(await Run("""[ { "locate": { "var": "x", "selector": "#a", "in": "frame" } } ]"""))
            .Code.ShouldBe(InterpreterErrorCodes.NotSupportedInV0);
        Fail(await Run("""[ { "click": { "selector": "#a", "in": "frame" } } ]"""))
            .Code.ShouldBe(InterpreterErrorCodes.NotSupportedInV0);
    }

    [Fact]
    public async Task Config_backend_expression_parse_and_eval_failures_are_terminal()
    {
        // A malformed config.backend expression fails at parse (before connect).
        Fail(await Run("[]", backendExpr: "1 +")).Code.ShouldBe("syntax_error");

        // A config.backend expression that parses but type-errors on evaluation.
        Fail(await Run("[]", backendExpr: "1 - 'x'")).Code.ShouldBe("type_error");
    }

    [Fact]
    public async Task Unknown_node_is_terminal()
    {
        var failure = Fail(await Run("""[ { "frobnicate": { } } ]"""));
        failure.Code.ShouldBe(InterpreterErrorCodes.UnknownNode);
        failure.AtStep.Kind.ShouldBe("frobnicate");
    }

    [Fact]
    public async Task A_handle_in_the_result_is_terminal()
    {
        Fail(await Run(LocateRowsOnly(), result: "rows")).Code.ShouldBe(InterpreterErrorCodes.HandleInResult);

        static string LocateRowsOnly() => $"[ {CapHome.LocateRows} ]";
    }

    [Fact]
    public async Task Backend_binding_must_resolve_to_an_adapter()
    {
        Fail(await Run("[]", backendExpr: "5")).Code.ShouldBe(InterpreterErrorCodes.InvalidBackendBinding);

        const string NoAdapter = """{ "backend": { "options": { "fixture": "caphome-search" } } }""";
        Fail(await Run("[]", inputs: NoAdapter)).Code.ShouldBe(InterpreterErrorCodes.InvalidBackendBinding);
    }

    [Fact]
    public async Task Expression_eval_and_parse_failures_are_terminal()
    {
        var eval = Fail(await Run("[]", result: "[1,2][5]"));
        eval.Class.ShouldBe("terminal");
        eval.Code.ShouldBe("index_out_of_range");

        Fail(await Run("[]", result: "1 +")).Code.ShouldBe("syntax_error");
    }

    [Fact]
    public async Task A_backend_timeout_is_retryable_exhausted()
    {
        var failure = Fail(await Run("""[ { "waitForRequest": { "urlPrefix": "https://no.match/", "method": "POST", "trigger": [] } } ]"""));
        failure.Class.ShouldBe("retryable-exhausted");
        failure.Code.ShouldBe("timeout");
    }

    [Fact]
    public async Task A_missing_fixture_is_a_terminal_backend_error()
    {
        const string BadFixture = """{ "backend": { "adapter": "fake", "options": { "fixture": "no-such-fixture" } } }""";
        Fail(await Run("[]", inputs: BadFixture)).Code.ShouldBe("backend_unavailable");
    }

    [Fact]
    public async Task Wait_for_request_without_a_method_matches_any()
    {
        var noMethod = CapHome.ToResults.Replace("\"method\": \"POST\", ", string.Empty, StringComparison.Ordinal);
        Ok(await Run($"[ {noMethod} ]", result: "pageUrl()")).GetString()!.ShouldContain("CapHome");
    }

    [Fact]
    public async Task Stats_count_executed_nodes_and_requests_through_the_frozen_clock()
    {
        var outcome = await Run("""[ { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement" } } ]""");
        outcome.Status.ShouldBe(RunStatus.Succeeded);
        outcome.Stats.Steps.ShouldBe(1);
        outcome.Stats.Requests.ShouldBe(1);
        outcome.Stats.DurationMs.ShouldBe(0);
        outcome.Stats.CacheHits.ShouldBe(0);
        outcome.Stats.Downloads.ShouldBe(0);
    }

    [Fact]
    public async Task Top_level_break_and_continue_are_no_ops_outside_a_loop()
    {
        Ok(await Run("""[ { "break": { } }, { "set": { "var": "x", "value": "'reached'" } } ]""", result: "x")).GetString().ShouldBe("reached");

        // An unconditional continue inside a forEach skips the rest of every body.
        Ok(await Run(
            """[ { "forEach": { "in": "['a','b']", "as": "i", "maxIterations": 10, "do": [ { "continue": { } }, { "push": { "into": "acc", "value": "i" } } ] } } ]""",
            result: "count(acc)", vars: """{ "acc": [] }"""))
            .GetInt64().ShouldBe(0);
    }

    [Fact]
    public async Task Loop_variable_shadows_and_unshadows_around_the_body()
    {
        // Outer i is shadowed by the loop var, then restored to its pre-loop value afterwards.
        var steps =
            """
            [ { "set": { "var": "i", "value": "'outer'" } },
              { "loop": { "maxIterations": 10, "for": { "var": "i", "from": "0", "to": "2" }, "do": [ { "push": { "into": "seen", "value": "i" } } ] } },
              { "push": { "into": "seen", "value": "i" } } ]
            """;
        Ok(await Run(steps, result: "seen", vars: """{ "seen": [] }"""))
            .EnumerateArray().Select(e => e.ToString()).ShouldBe(["0", "1", "outer"]);
    }
}
