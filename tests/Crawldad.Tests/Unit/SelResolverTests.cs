using System.Text.Json;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Browser;

namespace Crawldad.Tests.Unit;

/// <summary>The selector resolver (§5.2): structured <c>Sel</c> maps (css/title/base/nth/first/filter, frames
/// rejected), node selectors from JSON (string with var-first precedence, or a structured object), and the
/// per-field evaluation.</summary>
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

    [Fact]
    public async Task ResolveMap_rejects_frames_and_rootless_maps()
    {
        var scope = await ScopeAsync();

        Should.Throw<InterpreterException>(() => scope.Sel.ResolveMap(Map(("in", "frame"), ("css", "x"))))
            .Code.ShouldBe(InterpreterErrorCodes.NotSupportedInV0);
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
        (await (await scope.Sel.ResolveNodeAsync(Json("\"rows\""), CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(15);
        // a non-var string ⇒ treated as a CSS selector
        (await (await scope.Sel.ResolveNodeAsync(Json($"\"{CapHome.GridRows}\""), CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(15);
    }

    [Fact]
    public async Task ResolveNode_rejects_non_string_non_object_selectors()
    {
        var scope = await ScopeAsync();
        var error = await Should.ThrowAsync<InterpreterException>(async () => await scope.Sel.ResolveNodeAsync(Json("5"), CapHome.Ct));
        error.Code.ShouldBe(InterpreterErrorCodes.MalformedNode);
    }

    [Fact]
    public async Task ResolveNode_object_evaluates_each_field()
    {
        var scope = await ScopeAsync();

        (await (await scope.Sel.ResolveNodeAsync(Json($$"""{ "css": "{{CapHome.GridRows}}" }"""), CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(15);
        (await (await scope.Sel.ResolveNodeAsync(Json("""{ "title": "No such title" }"""), CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(0);
        (await (await scope.Sel.ResolveNodeAsync(Json("""{ "base": "rows", "css": "td:nth-child(2)" }"""), CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(10);
        (await (await scope.Sel.ResolveNodeAsync(Json($$"""{ "css": "{{CapHome.GridRows}}", "nth": "1 + 2" }"""), CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(1);
        (await (await scope.Sel.ResolveNodeAsync(Json($$"""{ "css": "{{CapHome.GridRows}}", "first": true }"""), CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(1);
        (await (await scope.Sel.ResolveNodeAsync(Json($$"""{ "css": "{{CapHome.GridRows}}", "filter": { "hasTextRegex": "Void" } }"""), CapHome.Ct)).CountAsync(CapHome.Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task ResolveNode_object_rejects_frames_and_unknown_keys()
    {
        var scope = await ScopeAsync();

        (await Should.ThrowAsync<InterpreterException>(async () =>
            await scope.Sel.ResolveNodeAsync(Json("""{ "in": "f", "css": "x" }"""), CapHome.Ct))).Code.ShouldBe(InterpreterErrorCodes.NotSupportedInV0);
        (await Should.ThrowAsync<InterpreterException>(async () =>
            await scope.Sel.ResolveNodeAsync(Json("""{ "bogus": "x" }"""), CapHome.Ct))).Code.ShouldBe(InterpreterErrorCodes.MalformedNode);
    }
}
