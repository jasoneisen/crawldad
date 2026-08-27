using Crawldad.Api.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

public class GrammarAndOperatorTests
{
    [Theory]
    // literals
    [InlineData("1", 1L)]
    [InlineData("1.5", 1.5d)]
    [InlineData("'hello'", "hello")]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", null)]
    // integer arithmetic (C# semantics: integer division + remainder)
    [InlineData("1 + 2", 3L)]
    [InlineData("5 - 3", 2L)]
    [InlineData("4 * 3", 12L)]
    [InlineData("7 / 2", 3L)]
    [InlineData("7 % 3", 1L)]
    [InlineData("-5", -5L)]
    [InlineData("3 - -1", 4L)]
    // double / mixed arithmetic (either side double ⇒ double)
    [InlineData("1.5 + 2.5", 4.0d)]
    [InlineData("1 + 2.5", 3.5d)]
    [InlineData("5.0 / 2", 2.5d)]
    [InlineData("5.0 % 2", 1.0d)]
    [InlineData("6 * 1.5", 9.0d)]
    [InlineData("-1.5", -1.5d)]
    // '+' concatenation with string(x) conversion of the other side
    [InlineData("'a' + 'b'", "ab")]
    [InlineData("'n=' + 5", "n=5")]
    [InlineData("5 + 'x'", "5x")]
    [InlineData("'v=' + true", "v=true")]
    [InlineData("'v=' + false", "v=false")]
    [InlineData("'v=' + null", "v=")]
    [InlineData("'' + 1.5", "1.5")]
    // equality (null-safe, numeric-cross, ordinal string, bool, mismatch ⇒ false)
    [InlineData("1 == 1", true)]
    [InlineData("1 != 2", true)]
    [InlineData("1 == 1.0", true)]
    [InlineData("1.0 != 2", true)]
    [InlineData("'a' == 'a'", true)]
    [InlineData("'a' == 'b'", false)]
    [InlineData("true == true", true)]
    [InlineData("true == false", false)]
    [InlineData("null == null", true)]
    [InlineData("null == 1", false)]
    [InlineData("1 == null", false)]
    [InlineData("1 == 'a'", false)]
    [InlineData("true == 1", false)]
    [InlineData("1 != 1", false)]
    // relational (numbers only)
    [InlineData("1 < 2", true)]
    [InlineData("2 < 1", false)]
    [InlineData("2 <= 2", true)]
    [InlineData("2 <= 1", false)]
    [InlineData("3 > 2", true)]
    [InlineData("2 > 3", false)]
    [InlineData("3 >= 3", true)]
    [InlineData("2 >= 3", false)]
    [InlineData("1.5 < 2", true)]
    [InlineData("2 > 1.5", true)]
    // logical + ternary + unary
    [InlineData("true && true", true)]
    [InlineData("true && false", false)]
    [InlineData("false && true", false)]
    [InlineData("true || false", true)]
    [InlineData("false || true", true)]
    [InlineData("false || false", false)]
    [InlineData("!true", false)]
    [InlineData("!false", true)]
    [InlineData("true ? 1 : 2", 1L)]
    [InlineData("false ? 1 : 2", 2L)]
    [InlineData("1 < 2 ? 'y' : 'n'", "y")]
    // precedence + associativity
    [InlineData("1 + 2 * 3", 7L)]
    [InlineData("(1 + 2) * 3", 9L)]
    [InlineData("1 + 2 == 3", true)]
    [InlineData("1 < 2 && 3 < 4", true)]
    [InlineData("true || false && false", true)]
    [InlineData("1 - 2 - 3", -4L)]
    [InlineData("10 / 2 / 5", 1L)]
    [InlineData("true ? 1 : false ? 2 : 3", 1L)]
    [InlineData("false ? 1 : false ? 2 : 3", 3L)]
    [InlineData("!(1 == 2)", true)]
    public async Task Evaluates(string source, object? expected) =>
        (await Xp.EvalAsync(source)).ShouldBe(expected);

    [Fact]
    public async Task Long_division_by_zero_is_terminal() =>
        (await Xp.EvalErrorAsync("1 / 0")).Code.ShouldBe(ExpressionErrorCodes.DivisionByZero);

    [Fact]
    public async Task Long_remainder_by_zero_is_terminal() =>
        (await Xp.EvalErrorAsync("1 % 0")).Code.ShouldBe(ExpressionErrorCodes.DivisionByZero);

    [Fact]
    public async Task Double_division_by_zero_is_ieee_infinity() =>
        (await Xp.EvalAsync("1.0 / 0.0")).ShouldBe(double.PositiveInfinity);

    [Fact]
    public async Task Double_remainder_by_zero_is_ieee_nan() =>
        ((double)(await Xp.EvalAsync("5.0 % 0.0"))!).ShouldBe(double.NaN);

    [Fact]
    public async Task Short_circuit_and_does_not_evaluate_right()
    {
        // The right side would throw index_out_of_range if evaluated.
        (await Xp.EvalAsync("false && [1][9] == 0")).ShouldBe(false);
    }

    [Fact]
    public async Task Short_circuit_or_does_not_evaluate_right() =>
        (await Xp.EvalAsync("true || [1][9] == 0")).ShouldBe(true);

    [Fact]
    public async Task Ternary_only_evaluates_the_taken_branch() =>
        // The untaken branch would be a terminal index failure.
        (await Xp.EvalAsync("true ? 1 : [1][9]")).ShouldBe(1L);

    [Fact]
    public async Task Empty_array_literal_is_empty_list() =>
        (await Xp.EvalAsync("[]")).ShouldBeAssignableTo<List<object?>>()!.ShouldBeEmpty();

    [Fact]
    public async Task Array_literal_collects_elements()
    {
        var list = (await Xp.EvalAsync("[1, 'a', true, null]")).ShouldBeAssignableTo<List<object?>>()!;
        list.ShouldBe([1L, "a", true, null]);
    }

    [Fact]
    public async Task Empty_object_literal_is_empty_map() =>
        (await Xp.EvalAsync("{}")).ShouldBeAssignableTo<Dictionary<string, object?>>()!.ShouldBeEmpty();

    [Fact]
    public async Task Object_literal_with_identifier_and_quoted_keys_is_insertion_ordered()
    {
        var map = (await Xp.EvalAsync("{ a: 1, 'b c': 2, d: 'x' }")).ShouldBeAssignableTo<Dictionary<string, object?>>()!;
        map.Keys.ShouldBe(["a", "b c", "d"]);
        map["a"].ShouldBe(1L);
        map["b c"].ShouldBe(2L);
        map["d"].ShouldBe("x");
    }

    [Fact]
    public async Task Identifier_resolves_from_scope()
    {
        var scope = new FakeScope().With("x", 42L);
        (await Xp.EvalAsync("x + 1", scope)).ShouldBe(43L);
    }

    [Fact]
    public async Task Identifiers_may_start_with_or_contain_underscores_and_digits()
    {
        var scope = new FakeScope().With("_flag", true).With("a_1", 2L);
        (await Xp.EvalAsync("_flag", scope)).ShouldBe(true);
        (await Xp.EvalAsync("a_1 + 1", scope)).ShouldBe(3L);
    }

    [Fact]
    public async Task Member_access_on_map_returns_value_and_null_for_absent_key()
    {
        var scope = new FakeScope().With("m", new Dictionary<string, object?>(StringComparer.Ordinal) { ["k"] = "v" });
        (await Xp.EvalAsync("m.k", scope)).ShouldBe("v");
        (await Xp.EvalAsync("m.missing", scope)).ShouldBeNull();
    }

    [Fact]
    public async Task Member_access_on_null_is_null()
    {
        var scope = new FakeScope().With("m", null);
        (await Xp.EvalAsync("m.anything", scope)).ShouldBeNull();
    }

    [Fact]
    public async Task Chained_member_access()
    {
        var inner = new Dictionary<string, object?>(StringComparer.Ordinal) { ["b"] = 7L };
        var scope = new FakeScope().With("a", new Dictionary<string, object?>(StringComparer.Ordinal) { ["inner"] = inner });
        (await Xp.EvalAsync("a.inner.b", scope)).ShouldBe(7L);
    }

    [Fact]
    public async Task Index_into_array_with_long_and_integral_double()
    {
        (await Xp.EvalAsync("[10, 20, 30][1]")).ShouldBe(20L);
        (await Xp.EvalAsync("[10, 20, 30][1.0]")).ShouldBe(20L);
    }

    [Fact]
    public async Task Index_into_map_with_string_returns_value_or_null()
    {
        var scope = new FakeScope().With("m", new Dictionary<string, object?>(StringComparer.Ordinal) { ["k"] = 9L });
        (await Xp.EvalAsync("m['k']", scope)).ShouldBe(9L);
        (await Xp.EvalAsync("m['nope']", scope)).ShouldBeNull();
    }

    [Fact]
    public async Task Index_then_member_chain()
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal) { ["url"] = "u1" };
        var scope = new FakeScope().With("rows", new List<object?> { row });
        (await Xp.EvalAsync("rows[0].url", scope)).ShouldBe("u1");
    }
}
