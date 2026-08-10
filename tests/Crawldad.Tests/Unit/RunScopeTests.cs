using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Browser;

namespace Crawldad.Tests.Unit;

/// <summary>The flat run scope: set/push, loop-variable shadow/unshadow, and the read-only DOM access with
/// null-propagation for the non-nullable innerText/innerHtml seams.</summary>
public class RunScopeTests
{
    private static RunScope Empty() => new(new Dictionary<string, object?>(StringComparer.Ordinal));

    private static async Task<RunScope> ResultsScopeAsync()
    {
        var page = await Runner.FakePageAsync();
        await page.Locator("#ctl00_PlaceHolderMain_btnNewSearch").ClickAsync(null, CapHome.Ct);
        var scope = Empty();
        scope.Bind(page);
        return scope;
    }

    [Fact]
    public void Set_and_resolve_round_trip_and_input_is_seeded()
    {
        var scope = new RunScope(new Dictionary<string, object?>(StringComparer.Ordinal) { ["k"] = "v" });
        scope.TryResolve("input", out var input).ShouldBeTrue();
        ((Dictionary<string, object?>)input!)["k"].ShouldBe("v");

        scope.Set("x", 5L);
        scope.TryResolve("x", out var x).ShouldBeTrue();
        x.ShouldBe(5L);
        scope.TryResolve("missing", out _).ShouldBeFalse();
    }

    [Fact]
    public void Push_appends_to_an_array_and_rejects_bad_targets()
    {
        var scope = Empty();
        scope.Set("list", new List<object?>());
        scope.Push("list", 1L);
        scope.Push("list", 2L);
        scope.TryResolve("list", out var list);
        ((List<object?>)list!).ShouldBe([1L, 2L]);

        Should.Throw<InterpreterException>(() => scope.Push("undefined", 1L)).Code.ShouldBe(InterpreterErrorCodes.UndefinedPushTarget);
        scope.Set("scalar", 7L);
        Should.Throw<InterpreterException>(() => scope.Push("scalar", 1L)).Code.ShouldBe(InterpreterErrorCodes.UndefinedPushTarget);
    }

    [Fact]
    public void Shadow_restores_prior_bindings_and_removes_new_ones()
    {
        var scope = Empty();
        scope.Set("x", 1L);

        using (scope.Shadow(("x", 2L), ("y", 99L)))
        {
            scope.TryResolve("x", out var x);
            x.ShouldBe(2L);
            scope.TryResolve("y", out var y);
            y.ShouldBe(99L);
        }

        scope.TryResolve("x", out var xAfter);
        xAfter.ShouldBe(1L);
        scope.TryResolve("y", out _).ShouldBeFalse();
    }

    [Fact]
    public void PageUrl_before_bind_throws()
    {
        var scope = Empty();
        Should.Throw<InvalidOperationException>(() => scope.PageUrl());
    }

    [Fact]
    public async Task Dom_count_and_exists()
    {
        var scope = await ResultsScopeAsync();
        (await scope.Dom.CountAsync(CapHome.GridRows, null, CapHome.Ct)).ShouldBe(15);
        (await scope.Dom.ExistsAsync("#divGlobalLoading", null, CapHome.Ct)).ShouldBeTrue();
        (await scope.Dom.ExistsAsync("#absent", null, CapHome.Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Dom_text_reads_raw_and_null_propagates()
    {
        var scope = await ResultsScopeAsync();
        // 4th tr (data row 1), 2nd td = date cell, raw (untrimmed) textContent
        (await scope.Dom.TextAsync($"{CapHome.GridRows}:nth-child(4)", "td:nth-child(2)", CapHome.Ct)).ShouldBe("  01/03/2024  ");
        (await scope.Dom.TextAsync("#absent", null, CapHome.Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Dom_innerText_and_innerHtml_null_propagate_when_absent()
    {
        var scope = await ResultsScopeAsync();
        var present = $"{CapHome.GridRows}:nth-child(4)";

        (await scope.Dom.InnerTextAsync(present, "td:nth-child(4)", CapHome.Ct)).ShouldBe("Enforcement");
        (await scope.Dom.InnerHtmlAsync(present, "td:nth-child(4)", CapHome.Ct)).ShouldBe("Enforcement");
        (await scope.Dom.InnerTextAsync("#absent", null, CapHome.Ct)).ShouldBeNull();
        (await scope.Dom.InnerHtmlAsync("#absent", null, CapHome.Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Dom_attr_reads_or_null_propagates_and_accepts_a_handle_target()
    {
        var scope = await ResultsScopeAsync();
        (await scope.Dom.AttrAsync($"{CapHome.GridRows}:nth-child(4)", "td:nth-child(3) a", "href", CapHome.Ct))
            .ShouldBe("/LJCMG/Cap/CapDetail.aspx?id=1");
        (await scope.Dom.AttrAsync("#absent", null, "href", CapHome.Ct)).ShouldBeNull();

        // a handle target (the value model's opaque-handle shape) resolves directly
        ILocatorHandle handle = scope.PageHandle.Locator(CapHome.GridRows);
        (await scope.Dom.CountAsync(handle, null, CapHome.Ct)).ShouldBe(15);

        // a structured-Sel map target (as a DOM builtin would pass) routes through the resolver
        var map = new Dictionary<string, object?>(StringComparer.Ordinal) { ["css"] = CapHome.GridRows };
        (await scope.Dom.CountAsync(map, null, CapHome.Ct)).ShouldBe(15);
    }
}
