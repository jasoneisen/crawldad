using Crawldad.Api.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

/// <summary>The binding surface — filter/map/any/all/sortBy — and the child-scope (BindingScope) semantics they
/// introduce: per-element binding, outer-name shadowing, parent delegation (vars/pageUrl/DOM), and nesting.</summary>
public class BindingBuiltinTests
{
    [Theory]
    // filter
    [InlineData("length(filter([1, 2, 3, 4], n, n < 3))", 2L)]
    [InlineData("filter([1, 2, 3, 4], n, n < 3)[1]", 2L)]
    [InlineData("length(filter([], n, n < 3))", 0L)]
    [InlineData("filter(null, n, n < 3)", null)]
    // map
    [InlineData("map([1, 2, 3], n, n * 10)[2]", 30L)]
    [InlineData("length(map([], n, n))", 0L)]
    [InlineData("map(null, n, n)", null)]
    // any — short-circuits, empty → false
    [InlineData("any([1, 2, 3], n, n == 2)", true)]
    [InlineData("any([1, 2, 3], n, n == 9)", false)]
    [InlineData("any([], n, n == 1)", false)]
    [InlineData("any(null, n, n == 1)", null)]
    // all — short-circuits, empty → true (vacuous)
    [InlineData("all([2, 4, 6], n, n < 10)", true)]
    [InlineData("all([2, 4, 60], n, n < 10)", false)]
    [InlineData("all([], n, n < 10)", true)]
    [InlineData("all(null, n, n < 10)", null)]
    // sortBy — ascending numeric / ordinal string
    [InlineData("sortBy([3, 1, 2], n, n)[0]", 1L)]
    [InlineData("sortBy(['c', 'a', 'b'], s, s)[0]", "a")]
    [InlineData("sortBy([3, 1, 2], n, 0 - n)[0]", 3L)]
    [InlineData("length(sortBy([], n, n))", 0L)]
    [InlineData("sortBy(null, n, n)", null)]
    public async Task Binding_builtins(string source, object? expected) =>
        (await Xp.EvalAsync(source)).ShouldBe(expected);

    [Theory]
    // primary must be an array
    [InlineData("filter('s', n, true)", ExpressionErrorCodes.TypeError)]
    [InlineData("map('s', n, n)", ExpressionErrorCodes.TypeError)]
    [InlineData("any('s', n, true)", ExpressionErrorCodes.TypeError)]
    [InlineData("all('s', n, true)", ExpressionErrorCodes.TypeError)]
    [InlineData("sortBy('s', n, n)", ExpressionErrorCodes.TypeError)]
    // predicate must be a bool
    [InlineData("filter([1, 2], n, n)", ExpressionErrorCodes.TypeError)]
    [InlineData("any([1, 2], n, n)", ExpressionErrorCodes.TypeError)]
    [InlineData("all([1, 2], n, n)", ExpressionErrorCodes.TypeError)]
    // sortBy keys must be homogeneous, number-or-string
    [InlineData("sortBy([1, 'a'], n, n)", ExpressionErrorCodes.TypeError)]
    [InlineData("sortBy([1, 2], n, n == 1)", ExpressionErrorCodes.TypeError)]
    [InlineData("sortBy([1], n, [n])", ExpressionErrorCodes.TypeError)]
    public async Task Binding_builtin_terminal_failures(string source, string expectedCode) =>
        (await Xp.EvalErrorAsync(source)).Code.ShouldBe(expectedCode);

    [Fact]
    public async Task Binding_shadows_an_outer_variable_for_the_body_only()
    {
        // outer n = 99; inside filter, n is the per-element binding, not 99.
        var scope = new FakeScope().With("n", 99L);
        (await Xp.EvalAsync("length(filter([1, 2, 3], n, n < 3))", scope)).ShouldBe(2L);
        // outer n is restored/untouched after the binding builtin.
        (await Xp.EvalAsync("n", scope)).ShouldBe(99L);
    }

    [Fact]
    public async Task Body_resolves_outer_variables_through_the_parent_scope()
    {
        var scope = new FakeScope().With("limit", 3L).With("bump", 100L);
        (await Xp.EvalAsync("length(filter([1, 2, 3, 4], n, n < limit))", scope)).ShouldBe(2L);
        (await Xp.EvalAsync("map([1, 2], n, n + bump)[1]", scope)).ShouldBe(102L);
    }

    [Fact]
    public async Task Predicate_may_read_the_dom_through_the_child_scope()
    {
        // The binding scope delegates DOM access to the parent, so content-aware predicates are legal.
        var dom = new FakeDom
        {
            OnCount = static (target, _) => target is string s && string.Equals(s, "keep", StringComparison.Ordinal) ? 1L : 0L,
        };
        var scope = new FakeScope(dom);
        (await Xp.EvalAsync("length(filter(['keep', 'drop', 'keep'], sel, count(sel) > 0))", scope)).ShouldBe(2L);
    }

    [Fact]
    public async Task Body_may_read_pageUrl_through_the_child_scope()
    {
        var scope = new FakeScope(pageUrl: "https://host/p");
        (await Xp.EvalAsync("map([1, 2], n, urlHost(pageUrl()))[0]", scope)).ShouldBe("host");
    }

    [Fact]
    public async Task Nested_binding_builtins_are_the_flagship_indent_query()
    {
        var scope = new FakeScope()
            .With("parents", Val.Map(("0", "REC-A"), ("12", "REC-B"), ("24", "REC-C")))
            .With("indent", 15L);

        var cand = (await Xp.EvalAsync("filter(map(keys(parents), k, toInt(k)), n, n < indent)", scope))
            .ShouldBeAssignableTo<List<object?>>()!;
        cand.ShouldBe([0L, 12L]);
    }

    [Fact]
    public async Task Sort_by_is_stable_across_equal_keys()
    {
        // Elements tagged a..e with sort keys [2,1,2,1,2]; a stable ascending sort yields b,d (key 1) then a,c,e (key 2).
        var scope = new FakeScope().With("rows", Val.List(
            Val.Map(("id", "a"), ("k", 2L)),
            Val.Map(("id", "b"), ("k", 1L)),
            Val.Map(("id", "c"), ("k", 2L)),
            Val.Map(("id", "d"), ("k", 1L)),
            Val.Map(("id", "e"), ("k", 2L))));

        var ordered = (await Xp.EvalAsync("map(sortBy(rows, r, r.k), r, r.id)", scope))
            .ShouldBeAssignableTo<List<object?>>()!;
        ordered.ShouldBe(["b", "d", "a", "c", "e"]);
    }

    [Fact]
    public async Task Filter_and_map_produce_fresh_lists_and_do_not_touch_the_source()
    {
        var scope = new FakeScope().With("xs", Val.List(1L, 2L, 3L));
        (await Xp.EvalAsync("length(filter(xs, n, n > 1))", scope)).ShouldBe(2L);
        (await Xp.EvalAsync("length(xs)", scope)).ShouldBe(3L); // source unchanged
    }
}
