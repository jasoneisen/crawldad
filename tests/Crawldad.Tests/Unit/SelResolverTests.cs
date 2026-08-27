using System.Text.Json;
using Crawldad.Api.Features.Runs.Interpreter;
using Crawldad.Api.Features.Runs.Interpreter.Expressions;
using Crawldad.Api.Infrastructure.Browser;
using Crawldad.Tests.Support;
using Crawldad.Tests.Unit.Expressions;

namespace Crawldad.Tests.Unit;

/// <summary>The selector resolver: structured <c>Sel</c> maps (css/title/base/nth/first/filter/in), node selectors from
/// JSON (string with var-first precedence, or a structured object), and the per-field evaluation. Frame resolution proper
/// is exercised end-to-end by <see cref="FrameNodeTests"/>; here an <c>in:</c> naming an unbound var is a terminal <c>malformed_node</c>.</summary>
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
        scope.Set("boundFrame", page.FrameLocator("#f")); // an IFrameHandle (bound by `frame`) — NOT an ILocatorHandle
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

    // A structured Sel nth is an already-evaluated Expr result narrowed by .Nth — a non-integer (a fractional double, or a
    // non-number string/null/bool from a computed Expr) is a terminal type_error, never an unhandled InvalidCastException or
    // NullReferenceException. This is the sibling of the from-handle nth cast, classifying through ExpressionValues.RequireNthIndex.
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

    // A type-correct integer outside the valid 0-based 32-bit range is index_out_of_range — a negative index (the
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

    // An integral-VALUED double nth coerces to the same index a long does (3.0 ≡ 3L), so it narrows identically —
    // the accepted-domain coercion parity the fix preserves on the fake (real≡fake for the accepted 0-based domain).
    [Fact]
    public async Task ResolveMap_nth_integral_double_coerces_like_a_long()
    {
        var scope = await ScopeAsync();

        (await scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("nth", 3.0))).CountAsync(CapHome.Ct)).ShouldBe(1);
        (await scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("nth", 3L))).CountAsync(CapHome.Ct)).ShouldBe(1);
    }

    // A structured Sel `first` is a lazy .First narrowing keyed on a bool. Via the expression path its value is an
    // already-evaluated, UNCOERCED Expr result — so a non-bool is a terminal type_error through
    // ExpressionValues.RequireFirstFlag, never an unhandled exception. The node path feeds a schema-checked JSON bool, unaffected.
    [Fact]
    public async Task ResolveMap_first_non_bool_is_a_terminal_type_error()
    {
        var scope = await ScopeAsync();

        Should.Throw<ExpressionEvaluationException>(() => scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("first", "x"))))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);          // string ("first: 'true'"-style typo)
        Should.Throw<ExpressionEvaluationException>(() => scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("first", 1L))))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);          // number
        Should.Throw<ExpressionEvaluationException>(() => scope.Sel.ResolveMap(Map(("css", CapHome.GridRows), ("first", null))))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);          // null (the raw-unbox NullReferenceException case)
    }

    // EVERY string-typed Sel field the resolver reads from an (uncoerced) object-literal target — the css/xpath/text/role/
    // title/base/in roots, a base-relative css, a role's accessible `name`, and a malformed `filter` — classifies a
    // non-string through ExpressionValues.RequireString (terminal type_error). The node path coerces these to strings and is schema-checked, so it is unaffected.
    [Fact]
    public async Task ResolveMap_non_string_field_or_malformed_filter_is_a_terminal_type_error()
    {
        var scope = await ScopeAsync();

        void Rejects(params (string Key, object? Value)[] entries) =>
            Should.Throw<ExpressionEvaluationException>(() => scope.Sel.ResolveMap(Map(entries)))
                .Code.ShouldBe(ExpressionErrorCodes.TypeError);

        Rejects(("css", 1L));                                        // css root
        Rejects(("xpath", true));                                    // xpath root
        Rejects(("text", 1L));                                       // text root
        Rejects(("title", 2.5));                                     // title root
        Rejects(("role", 1L));                                       // role root
        Rejects(("role", "button"), ("name", 1L));                  // role's accessible name
        Rejects(("base", 1L));                                       // base handle var name
        Rejects(("base", "rows"), ("css", 1L));                     // base-relative css
        Rejects(("in", 1L), ("css", "x"));                          // frame var name (read before the root)
        Rejects(("css", CapHome.GridRows), ("filter", "x"));        // filter must be an object
        Rejects(("css", CapHome.GridRows), ("filter", Map()));      // filter missing hasTextRegex
        Rejects(("css", CapHome.GridRows), ("filter", Map(("hasTextRegex", 1L)))); // hasTextRegex must be a string
    }

    // The same classification reached END TO END through each DOM-read consumer (exists/text/attr) that funnels an
    // object-literal target into the resolver, e.g. exists({ css:'tr', first:'x' }). Each terminates as a type_error, never a 500.
    [Theory]
    [InlineData("exists({ css: 'tr', first: 'x' })")]   // string
    [InlineData("text({ css: 'tr', first: 1 })")]       // number
    [InlineData("attr({ css: 'tr', first: null }, 'href')")] // null
    public async Task Dom_read_with_a_non_bool_sel_first_is_a_terminal_type_error(string source)
    {
        var scope = await ScopeAsync();
        (await Xp.EvalErrorAsync(source, scope)).Code.ShouldBe(ExpressionErrorCodes.TypeError);
    }

    // The happy path: an expression-COMPUTED bool `first` (not just a bool literal) still narrows — RequireFirstFlag
    // passes any real bool through unchanged, however it was produced (the guard adds no restriction to valid input).
    [Fact]
    public async Task ResolveMap_first_accepts_an_expression_computed_bool()
    {
        var scope = await ScopeAsync();
        (await Xp.EvalAsync("exists({ css: 'tr', first: 1 == 1 })", scope)).ShouldBeOfType<bool>();
    }

    // The DOM-read TARGET itself: RequireDomTarget's catch-all admits ANY opaque handle, and the value model's second
    // handle type — an IFrameHandle bound by `frame` — reaches ResolveBase's cast via a bare var in a target position
    // with no type gate. A frame handle (or any other non-locator opaque handle) is now a terminal type_error; a real locator handle target still resolves.
    [Fact]
    public async Task ResolveTarget_non_locator_handle_is_a_terminal_type_error()
    {
        var scope = await ScopeAsync();

        Should.Throw<ExpressionEvaluationException>(() => scope.Sel.ResolveTarget(scope.Sel.RequireFrame("boundFrame"), null))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);      // a frame handle (bound by `frame`) as a target
        Should.Throw<ExpressionEvaluationException>(() => scope.Sel.ResolveTarget(new FakeHandle(), null))
            .Code.ShouldBe(ExpressionErrorCodes.TypeError);      // any other non-locator opaque handle
        (await scope.Sel.ResolveTarget(scope.Sel.RequireHandle("rows"), null).CountAsync(CapHome.Ct)).ShouldBe(15); // a locator handle still resolves
    }

    // The same classification reached END TO END — a frame handle flowed into a DOM builtin.
    [Fact]
    public async Task Dom_read_with_a_frame_handle_target_is_a_terminal_type_error()
    {
        var scope = await ScopeAsync();
        (await Xp.EvalErrorAsync("exists(boundFrame)", scope)).Code.ShouldBe(ExpressionErrorCodes.TypeError);
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
