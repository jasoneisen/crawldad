using System.Text.Json;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit;

/// <summary>Interpreter v0 node dispatch and control flow beyond the acceptance fragment: if/else, loop bounds + break +
/// caps, forEach over arrays and locators, shadowing, the not-supported-in-v0 guards, stats, and failure classification.
/// Each case runs a small payload against the fake backend.</summary>
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

    // A typed JSON number bound behaves exactly as the Expr string "N": the same loop with typed from/to/step and
    // with the equivalent Expr-string bounds yields an identical [0, 2, 4].
    [Fact]
    public async Task Loop_for_typed_number_bounds_match_the_expr_string_form()
    {
        const string Typed = """[ { "loop": { "maxIterations": 100, "for": { "var": "i", "from": 0, "to": 4, "inclusiveTo": true, "step": 2 }, "do": [ { "push": { "into": "acc", "value": "i" } } ] } } ]""";
        const string Strung = """[ { "loop": { "maxIterations": 100, "for": { "var": "i", "from": "0", "to": "4", "inclusiveTo": true, "step": "2" }, "do": [ { "push": { "into": "acc", "value": "i" } } ] } } ]""";

        Longs(Ok(await Run(Typed, result: "acc", vars: """{ "acc": [] }"""))).ShouldBe([0, 2, 4]);
        Longs(Ok(await Run(Strung, result: "acc", vars: """{ "acc": [] }"""))).ShouldBe([0, 2, 4]);
    }

    // A typed negative step decrements i exactly as the Expr "-1" does; with from ≤ to the loop runs to its cap, and
    // onMaxIterations:"warn" stops it cleanly — [0, -1, -2] for both the typed and the string form.
    [Fact]
    public async Task Loop_for_typed_negative_step_matches_the_expr_string_form()
    {
        const string Typed = """[ { "loop": { "maxIterations": 3, "onMaxIterations": "warn", "for": { "var": "i", "from": 0, "to": 3, "step": -1 }, "do": [ { "push": { "into": "acc", "value": "i" } } ] } } ]""";
        const string Strung = """[ { "loop": { "maxIterations": 3, "onMaxIterations": "warn", "for": { "var": "i", "from": "0", "to": "3", "step": "-1" }, "do": [ { "push": { "into": "acc", "value": "i" } } ] } } ]""";

        Longs(Ok(await Run(Typed, result: "acc", vars: """{ "acc": [] }"""))).ShouldBe([0, -1, -2]);
        Longs(Ok(await Run(Strung, result: "acc", vars: """{ "acc": [] }"""))).ShouldBe([0, -1, -2]);
    }

    // A typed zero step never advances i — there is no save-time rejection (identical to the Expr "0" form), so the loop
    // runs to its maxIterations cap: terminal max_iterations_exceeded for both the typed 0 and the Expr "0".
    [Fact]
    public async Task Loop_for_typed_zero_step_hits_the_cap_like_the_expr_string_form()
    {
        Fail(await Run("""[ { "loop": { "maxIterations": 3, "for": { "var": "i", "from": 0, "to": 3, "step": 0 }, "do": [] } } ]"""))
            .Code.ShouldBe(InterpreterErrorCodes.MaxIterationsExceeded);
        Fail(await Run("""[ { "loop": { "maxIterations": 3, "for": { "var": "i", "from": "0", "to": "3", "step": "0" }, "do": [] } } ]"""))
            .Code.ShouldBe(InterpreterErrorCodes.MaxIterationsExceeded);
    }

    // A non-integral loop.for bound is a terminal type_error at run time (never an unhandled InvalidCastException); a
    // literal 2.5, an Expr "2.5" spelling, and a COMPUTED non-integral double (5.0/2 == 2.5) all classify the same, and
    // from/to/step each reach the check. This direct interpreter path skips the save-time walker, so even a literal bound is caught here.
    [Fact]
    public async Task Loop_for_non_integral_bound_is_a_terminal_type_error()
    {
        // to: typed 2.5 ≡ Expr "2.5" — both classified, same code.
        Fail(await Run("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": 2.5 }, "do": [] } } ]"""))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);
        Fail(await Run("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": "0", "to": "2.5" }, "do": [] } } ]"""))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);

        // to: a computed non-integral double (5.0 / 2 == 2.5).
        Fail(await Run("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": "5.0 / 2" }, "do": [] } } ]"""))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);

        // to: a computed infinity (1.0 / 0.0 is +Infinity for doubles, never an integer) — the IsInfinity guard routes it
        // to the same type_error rather than letting (long)Infinity produce a garbage counter.
        Fail(await Run("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": "1.0 / 0.0" }, "do": [] } } ]"""))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);

        // from and step each reach the same classification (from once at entry; step once before the loop).
        Fail(await Run("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 2.5, "to": 5 }, "do": [] } } ]"""))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);
        Fail(await Run("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": 5, "step": "3.0 / 2" }, "do": [] } } ]"""))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);
    }

    // A bound computing to a string, bool, or null also escaped as an unhandled exception — InvalidCastException for a
    // string/bool, NullReferenceException for null — all outside the catch filters. They are now the SAME terminal
    // type_error as a fractional number, in the one RequireIntegralBound helper.
    [Fact]
    public async Task Loop_for_non_numeric_bound_is_a_terminal_type_error()
    {
        Fail(await Run("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": "'nope'" }, "do": [] } } ]"""))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError); // string
        Fail(await Run("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": "null", "to": 5 }, "do": [] } } ]"""))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError); // null
        Fail(await Run("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": "true" }, "do": [] } } ]"""))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError); // bool
    }

    // An integral-VALUED double bound (2.0, or a computed 4.0/2 == 2.0) is accepted and coerced to the long the
    // counter uses — exactly as an array index coerces — so `from 0 to 4.0` yields [0,1,2,3] just like `to: 4`, and an
    // inclusive `to: 4.0/2` gives [0,1,2]. Only a FRACTIONAL bound rejects; the fix does not over-reject whole doubles.
    [Fact]
    public async Task Loop_for_integral_double_bound_is_accepted_and_coerced()
    {
        Longs(Ok(await Run(
            """[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": 4.0 }, "do": [ { "push": { "into": "acc", "value": "i" } } ] } } ]""",
            result: "acc", vars: """{ "acc": [] }"""))).ShouldBe([0, 1, 2, 3]);
        Longs(Ok(await Run(
            """[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": "4.0 / 2", "inclusiveTo": true }, "do": [ { "push": { "into": "acc", "value": "i" } } ] } } ]""",
            result: "acc", vars: """{ "acc": [] }"""))).ShouldBe([0, 1, 2]);
    }

    // A from-handle locate.nth over a first-bound `rows` handle: the nth Expr is `nthExpr`. The refinement is lazy, so a
    // classification failure surfaces at the second locate step (index 1) with no DOM touched.
    private static async Task<RunFailureDetail> NthFromHandle(string nthExpr) =>
        Fail(await Run($$"""[ { "locate": { "var": "rows", "selector": "tr" } }, { "locate": { "var": "x", "from": "rows", "nth": "{{nthExpr}}" } } ]"""));

    // A locate.nth that evaluates to a non-integer is a terminal type_error at run time, covering a literal 2.5, a
    // COMPUTED non-integral double, a computed +Infinity, and the non-numbers string/null/bool. The structured-Sel nth
    // (SelResolver, via a node selector) routes through the same RequireNthIndex, so it classifies identically.
    [Fact]
    public async Task Locate_nth_non_integral_or_non_numeric_is_a_terminal_type_error()
    {
        (await NthFromHandle("2.5")).Code.ShouldBe(ExpressionErrorCodes.TypeError);
        (await NthFromHandle("5.0 / 2")).Code.ShouldBe(ExpressionErrorCodes.TypeError);   // computed non-integral
        (await NthFromHandle("1.0 / 0.0")).Code.ShouldBe(ExpressionErrorCodes.TypeError); // +Infinity, never an integer
        (await NthFromHandle("'nope'")).Code.ShouldBe(ExpressionErrorCodes.TypeError);    // string
        (await NthFromHandle("null")).Code.ShouldBe(ExpressionErrorCodes.TypeError);      // null
        (await NthFromHandle("true")).Code.ShouldBe(ExpressionErrorCodes.TypeError);      // bool

        Fail(await Run("""[ { "click": { "selector": { "css": "tr", "nth": "2.5" } } } ]"""))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError); // structured Sel nth, same helper
    }

    // A type-correct integer OUTSIDE the valid 0-based 32-bit range is a terminal index_out_of_range: a negative index
    // is rejected before either backend runs (fake yields no match, Playwright's Nth(-1) is the last), and one past
    // int.MaxValue is rejected too (the (int) narrowing would otherwise truncate to a garbage index). Both nth surfaces reach it.
    [Fact]
    public async Task Locate_nth_negative_or_out_of_int_range_is_a_terminal_index_out_of_range()
    {
        (await NthFromHandle("-1")).Code.ShouldBe(ExpressionErrorCodes.IndexOutOfRange);
        (await NthFromHandle("3000000000")).Code.ShouldBe(ExpressionErrorCodes.IndexOutOfRange); // > int.MaxValue

        Fail(await Run("""[ { "click": { "selector": { "css": "tr", "nth": "-1" } } } ]"""))
            .Code.ShouldBe(ExpressionErrorCodes.IndexOutOfRange); // structured Sel nth, same helper
    }

    // An integral-VALUED double nth (6.0/2 == 3.0, or a bare 3) coerces to the int the .Nth refinement takes,
    // exactly as a long would — so the locate SUCCEEDS just as `nth: "3"` does. Only a fractional/negative/out-of-range
    // value rejects; the fix does not over-reject a whole-valued double.
    [Fact]
    public async Task Locate_nth_integral_double_is_accepted_and_coerced()
    {
        Ok(await Run("""[ { "locate": { "var": "rows", "selector": "tr" } }, { "locate": { "var": "x", "from": "rows", "nth": "6.0 / 2" } } ]"""));
        Ok(await Run("""[ { "locate": { "var": "rows", "selector": "tr" } }, { "locate": { "var": "x", "from": "rows", "nth": "3" } } ]"""));
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
