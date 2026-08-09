using System.Text.Json;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;
using Crawldad.Web.Infrastructure.Browser;

namespace Crawldad.Tests.Unit;

/// <summary>The selector resolver (§5.2): structured <c>Sel</c> maps (css/title/base/nth/first/filter/in), node
/// selectors from JSON (string with var-first precedence, or a structured object), and the per-field evaluation. Frame
/// resolution proper is exercised end-to-end by <see cref="FrameNodeTests"/>; here an <c>in:</c> naming an unbound var
/// is a terminal <c>malformed_node</c>.</summary>
public class SelResolverTests
{
    private static JsonElement Json(string source)
    {
        using var doc = JsonDocument.Parse(source);
        return doc.RootElement.Clone();
    }

    // A results-page scope with a bound `rows` handle over the grid.
    private static async Task<RunScope> ScopeAsync()
    {
        var page = await Runner.FakePageAsync();
        await page.Locator("#ctl00_PlaceHolderMain_btnNewSearch").ClickAsync(null, CapHome.Ct);
        var scope = new RunScope(new Dictionary<string, object?>(StringComparer.Ordinal));
        scope.Bind(page);
        scope.Set("rows", page.Locator(CapHome.GridRows));
        scope.Set("notAHandle", "oops");
        return scope;
    }

    private static Dictionary<string, object?> Map(params (string Key, object? Value)[] entries)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            map[key] = value;
        }

        return map;
    }

    [Fact]
    public async Task ResolveMap_css_title_and_base_roots()
    {
        var scope = await ScopeAsync();

        (await scope.Sel.ResolveMap(Map(("css", CapHome.GridRows))).CountAsync(CapHome.Ct)).ShouldBe(15);
        (await scope.Sel.ResolveMap(Map(("title", "No such title"))).CountAsync(CapHome.Ct)).ShouldBe(0);
        (await scope.Sel.ResolveMap(Map(("base", "rows"), ("css", "td:nth-child(2)"))).CountAsync(CapHome.Ct)).ShouldBe(10);
        (await scope.Sel.ResolveMap(Map(("base", "rows"))).CountAsync(CapHome.Ct)).ShouldBe(15); // base with no relative css
    }

    [Fact]
    public async Task ResolveMap_refinements_nth_first_filter()
    {
        var scope = await ScopeAsync();

        (await scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("nth", 3L))).CountAsync(CapHome.Ct)).ShouldBe(1);
        (await scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("first", true))).CountAsync(CapHome.Ct)).ShouldBe(1);
        (await scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("first", false))).CountAsync(CapHome.Ct)).ShouldBe(15); // first:false ignored
        (await scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("filter", Map(("hasTextRegex", "Void"))))).CountAsync(CapHome.Ct)).ShouldBe(1);
    }

    // #37: a structured Sel nth is an already-evaluated Expr result narrowed by .Nth — a non-integer (a fractional
    // double, or a non-number string/null/bool from a computed Expr) is a terminal type_error, never the raw (int)(long)
    // unbox that escaped ResolveMap as an unhandled 500 (InvalidCastException / NullReferenceException). This is the
    // sibling of the from-handle nth cast, and classifies through the same ExpressionValues.RequireNthIndex.
    [Fact]
    public async Task ResolveMap_nth_non_integral_or_non_numeric_is_a_terminal_type_error()
    {
        var scope = await ScopeAsync();

        Should.Throw<ExpressionEvaluationException>(() => scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("nth", 2.5))))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);          // fractional double
        Should.Throw<ExpressionEvaluationException>(() => scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("nth", "x"))))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);          // string
        Should.Throw<ExpressionEvaluationException>(() => scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("nth", null))))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);          // null
        Should.Throw<ExpressionEvaluationException>(() => scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("nth", true))))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);          // bool
    }

    // #37: a type-correct integer outside the valid 0-based 32-bit range is index_out_of_range — a negative index (the
    // backends diverge: the fake yields no match, Playwright counts from the end) or one past int.MaxValue.
    [Fact]
    public async Task ResolveMap_nth_negative_or_out_of_range_is_index_out_of_range()
    {
        var scope = await ScopeAsync();

        Should.Throw<ExpressionEvaluationException>(() => scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("nth", -1L))))
            .Code.ShouldBe(ExpressionErrorCodes.IndexOutOfRange);
        Should.Throw<ExpressionEvaluationException>(() => scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("nth", 3_000_000_000L))))
            .Code.ShouldBe(ExpressionErrorCodes.IndexOutOfRange); // > int.MaxValue
    }

    // #37: an integral-VALUED double nth coerces to the same index a long does (3.0 ≡ 3L), so it narrows identically —
    // the accepted-domain coercion parity the fix preserves on the fake (real≡fake for the accepted 0-based domain).
    [Fact]
    public async Task ResolveMap_nth_integral_double_coerces_like_a_long()
    {
        var scope = await ScopeAsync();

        (await scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("nth", 3.0))).CountAsync(CapHome.Ct)).ShouldBe(1);
        (await scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("nth", 3L))).CountAsync(CapHome.Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ResolveMap_rejects_unbound_frames_and_rootless_maps()
    {
        var scope = await ScopeAsync();

        // `in` names a frame var; "frame" is unbound ⇒ RequireFrame faults with malformed_node.
        Should.Throw<InterpreterException>(() => scope.Sel.ResolveMap(Map(("in", "frame"), ("css", "x"))))
            .Code.ShouldBe(InterpreterErrorCodes.MalformedNode);
        Should.Throw<InterpreterException>(() => scope.Sel.ResolveMap(Map(("nth", 0L))))
            .Code.ShouldBe(InterpreterErrorCodes.MalformedNode);
    }

    [Fact]
    public async Task RequireHandle_rejects_non_handle_and_unbound_names()
    {
        var scope = await ScopeAsync();
        Should.Throw<InterpreterException>(() => scope.Sel.RequireHandle("notAHandle")).Code.ShouldBe(InterpreterErrorCodes.MalformedNode);
        Should.Throw<InterpreterException>(() => scope.Sel.RequireHandle("unbound")).Code.ShouldBe(InterpreterErrorCodes.MalformedNode);
    }

    [Fact]
    public async Task ResolveNode_string_prefers_a_bound_handle_var_then_falls_back_to_css()
    {
        var scope = await ScopeAsync();

        // "rows" names a bound handle ⇒ used directly (var-first precedence)
        (await (await scope.Sel.ResolveNodeAsync(Json("\"rows\""), null, CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(15);
        // a non-var string ⇒ treated as a CSS selector
        (await (await scope.Sel.ResolveNodeAsync(Json($"\"{CapHome.GridRows}\""), null, CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(15);
    }

    [Fact]
    public async Task ResolveNode_rejects_non_string_non_object_selectors()
    {
        var scope = await ScopeAsync();
        var error = await Should.ThrowAsync<InterpreterException>(async () => await scope.Sel.ResolveNodeAsync(Json("5"), null, CapHome.Ct));
        error.Code.ShouldBe(InterpreterErrorCodes.MalformedNode);
    }

    [Fact]
    public async Task ResolveNode_object_evaluates_each_field()
    {
        var scope = await ScopeAsync();

        (await (await scope.Sel.ResolveNodeAsync(Json($$"""{ "css": "{{CapHome.GridRows}}" }"""), null, CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(15);
        (await (await scope.Sel.ResolveNodeAsync(Json("""{ "title": "No such title" }"""), null, CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(0);
        (await (await scope.Sel.ResolveNodeAsync(Json("""{ "base": "rows", "css": "td:nth-child(2)" }"""), null, CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(10);
        (await (await scope.Sel.ResolveNodeAsync(Json($$"""{ "css": "{{CapHome.GridRows}}", "nth": "1 + 2" }"""), null, CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(1);
        (await (await scope.Sel.ResolveNodeAsync(Json($$"""{ "css": "{{CapHome.GridRows}}", "first": true }"""), null, CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(1);
        (await (await scope.Sel.ResolveNodeAsync(Json($$"""{ "css": "{{CapHome.GridRows}}", "filter": { "hasTextRegex": "Void" } }"""), null, CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ResolveNode_object_rejects_unbound_frames_and_unknown_keys()
    {
        var scope = await ScopeAsync();

        // `in` on a structured Sel roots it in a frame var; "f" is unbound ⇒ malformed_node.
        (await Should.ThrowAsync<InterpreterException>(async () =>
            await scope.Sel.ResolveNodeAsync(Json("""{ "in": "f", "css": "x" }"""), null, CapHome.Ct))).Code.ShouldBe(InterpreterErrorCodes.MalformedNode);
        (await Should.ThrowAsync<InterpreterException>(async () =>
            await scope.Sel.ResolveNodeAsync(Json("""{ "bogus": "x" }"""), null, CapHome.Ct))).Code.ShouldBe(InterpreterErrorCodes.MalformedNode);
    }
}
