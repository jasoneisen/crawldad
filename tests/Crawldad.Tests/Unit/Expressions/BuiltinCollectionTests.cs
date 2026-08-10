using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

/// <summary>The collection surface: array/map access with primary null-propagation and terminal out-of-range/type
/// failures reproducing the reference's C# throws.</summary>
public class BuiltinCollectionTests
{
    [Theory]
    // first / last
    [InlineData("first([10, 20, 30])", 10L)]
    [InlineData("last([10, 20, 30])", 30L)]
    [InlineData("first(null)", null)]
    [InlineData("last(null)", null)]
    // nth — 0-based; accepts an integral double index
    [InlineData("nth([10, 20, 30], 0)", 10L)]
    [InlineData("nth([10, 20, 30], 2)", 30L)]
    [InlineData("nth([10, 20, 30], 1.0)", 20L)]
    [InlineData("nth(null, 0)", null)]
    // slice — (start, endExclusive); 2-arg runs to the end
    [InlineData("length(slice([1, 2, 3, 4], 1))", 3L)]
    [InlineData("slice([1, 2, 3, 4], 1)[0]", 2L)]
    [InlineData("length(slice([1, 2, 3, 4], 1, 3))", 2L)]
    [InlineData("slice([1, 2, 3, 4], 1, 3)[1]", 3L)]
    [InlineData("length(slice([1, 2, 3], 3))", 0L)]
    [InlineData("length(slice([1, 2, 3], 1, 1))", 0L)]
    [InlineData("slice(null, 0)", null)]
    // reverse
    [InlineData("reverse([1, 2, 3])[0]", 3L)]
    [InlineData("reverse(null)", null)]
    // distinct — first-occurrence order, cross-numeric dedup (1 == 1.0)
    [InlineData("length(distinct(['a', 'a', 'b', 'a', 'c']))", 3L)]
    [InlineData("distinct(['a', 'b', 'a'])[1]", "b")]
    [InlineData("length(distinct([1, 1.0, 2]))", 2L)]
    [InlineData("length(distinct([true, true, false]))", 2L)]
    [InlineData("length(distinct([1.5, 1.5]))", 1L)]
    [InlineData("length(distinct([null, null, 'x']))", 2L)]
    [InlineData("distinct(null)", null)]
    // min / max — return the extreme element (its own type preserved), incl. mixed int/double
    [InlineData("min([3, 1, 2])", 1L)]
    [InlineData("max([1, 3, 2])", 3L)]
    [InlineData("min([3, 1.5, 2])", 1.5d)]
    [InlineData("max([1, 3.5, 2])", 3.5d)]
    [InlineData("min([5])", 5L)]
    [InlineData("min(null)", null)]
    [InlineData("max(null)", null)]
    // keys / get
    [InlineData("length(keys({a: 1, b: 2, c: 3}))", 3L)]
    [InlineData("keys({a: 1, b: 2})[0]", "a")]
    [InlineData("keys(null)", null)]
    [InlineData("get({a: 1, b: 2}, 'b')", 2L)]
    [InlineData("get({a: 1}, 'missing')", null)]
    [InlineData("get(null, 'k')", null)]
    public async Task Collection_builtins(string source, object? expected) =>
        (await Xp.EvalAsync(source)).ShouldBe(expected);

    [Theory]
    // empty-collection element access is terminal (C# .First()/.Last()/.Min()/.Max() throw)
    [InlineData("first([])", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("last([])", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("min([])", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("max([])", ExpressionErrorCodes.IndexOutOfRange)]
    // nth out of range / bad index type
    [InlineData("nth([1, 2], 5)", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("nth([1, 2], -1)", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("nth([1, 2], 'x')", ExpressionErrorCodes.TypeError)]
    [InlineData("nth([1, 2], 1.5)", ExpressionErrorCodes.TypeError)]
    // slice out of range (start, endExclusive) — never clamped
    [InlineData("slice([1, 2, 3], -1)", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("slice([1, 2, 3], 4)", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("slice([1, 2, 3], 2, 1)", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("slice([1, 2, 3], 0, 4)", ExpressionErrorCodes.IndexOutOfRange)]
    // wrong container types
    [InlineData("first('notarray')", ExpressionErrorCodes.TypeError)]
    [InlineData("last(5)", ExpressionErrorCodes.TypeError)]
    [InlineData("nth('s', 0)", ExpressionErrorCodes.TypeError)]
    [InlineData("slice('s', 0)", ExpressionErrorCodes.TypeError)]
    [InlineData("reverse('s')", ExpressionErrorCodes.TypeError)]
    [InlineData("distinct('s')", ExpressionErrorCodes.TypeError)]
    [InlineData("min('s')", ExpressionErrorCodes.TypeError)]
    [InlineData("keys([1])", ExpressionErrorCodes.TypeError)]
    [InlineData("get([1], 'k')", ExpressionErrorCodes.TypeError)]
    [InlineData("get({a: 1}, 5)", ExpressionErrorCodes.TypeError)]
    // element type violations
    [InlineData("distinct([[1], [2]])", ExpressionErrorCodes.TypeError)]
    [InlineData("min(['a', 'b'])", ExpressionErrorCodes.TypeError)]
    [InlineData("max([1, 'b'])", ExpressionErrorCodes.TypeError)]
    public async Task Collection_builtin_terminal_failures(string source, string expectedCode) =>
        (await Xp.EvalErrorAsync(source)).Code.ShouldBe(expectedCode);

    [Fact]
    public async Task Distinct_preserves_first_occurrence_order()
    {
        var result = (await Xp.EvalAsync("distinct(['c', 'a', 'c', 'b', 'a'])")).ShouldBeAssignableTo<List<object?>>()!;
        result.ShouldBe(["c", "a", "b"]);
    }

    [Fact]
    public async Task Reverse_does_not_mutate_the_source()
    {
        var scope = new FakeScope().With("xs", Val.List(1L, 2L, 3L));
        (await Xp.EvalAsync("reverse(xs)[0]", scope)).ShouldBe(3L);
        (await Xp.EvalAsync("xs[0]", scope)).ShouldBe(1L); // original unchanged
    }

    [Fact]
    public async Task Keys_are_in_insertion_order()
    {
        var result = (await Xp.EvalAsync("keys({ z: 1, a: 2, m: 3 })")).ShouldBeAssignableTo<List<object?>>()!;
        result.ShouldBe(["z", "a", "m"]);
    }
}
