using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

public class ErrorSemanticsTests
{
    [Theory]
    // arithmetic requires numbers (covers each non-numeric TypeName in the message)
    [InlineData("true - 1", ExpressionErrorCodes.TypeError)]
    [InlineData("1.5 - 'x'", ExpressionErrorCodes.TypeError)]
    [InlineData("[1] - 1", ExpressionErrorCodes.TypeError)]
    [InlineData("{a:1} - 1", ExpressionErrorCodes.TypeError)]
    [InlineData("'a' * 2", ExpressionErrorCodes.TypeError)]
    [InlineData("'a' / 2", ExpressionErrorCodes.TypeError)]
    [InlineData("'a' % 2", ExpressionErrorCodes.TypeError)]
    // string(x) cannot convert containers
    [InlineData("string([1])", ExpressionErrorCodes.TypeError)]
    [InlineData("'x' + [1]", ExpressionErrorCodes.TypeError)]
    // logical / condition positions require bool
    [InlineData("1 && true", ExpressionErrorCodes.TypeError)]
    [InlineData("null && true", ExpressionErrorCodes.TypeError)]
    [InlineData("true && 3", ExpressionErrorCodes.TypeError)]
    [InlineData("false || 'x'", ExpressionErrorCodes.TypeError)]
    [InlineData("1 || true", ExpressionErrorCodes.TypeError)]
    [InlineData("'a' ? 1 : 2", ExpressionErrorCodes.TypeError)]
    [InlineData("!5", ExpressionErrorCodes.TypeError)]
    // relational requires numbers
    [InlineData("'a' < 'b'", ExpressionErrorCodes.TypeError)]
    [InlineData("1 < 'a'", ExpressionErrorCodes.TypeError)]
    [InlineData("null < 1", ExpressionErrorCodes.TypeError)]
    [InlineData("true > 1", ExpressionErrorCodes.TypeError)]
    // equality on array/map is a type error (but null-safe comparisons are not — see facts)
    [InlineData("[1] == [1]", ExpressionErrorCodes.TypeError)]
    [InlineData("{a:1} == {a:1}", ExpressionErrorCodes.TypeError)]
    // unary '-' requires a number
    [InlineData("-'x'", ExpressionErrorCodes.TypeError)]
    [InlineData("-true", ExpressionErrorCodes.TypeError)]
    [InlineData("-[1]", ExpressionErrorCodes.TypeError)]
    [InlineData("-null", ExpressionErrorCodes.TypeError)]
    // member access on non-map non-null
    [InlineData("(1).x", ExpressionErrorCodes.TypeError)]
    [InlineData("5.x", ExpressionErrorCodes.TypeError)]
    [InlineData("'s'.y", ExpressionErrorCodes.TypeError)]
    [InlineData("true.z", ExpressionErrorCodes.TypeError)]
    [InlineData("[1].w", ExpressionErrorCodes.TypeError)]
    // index type errors
    [InlineData("[1,2]['x']", ExpressionErrorCodes.TypeError)]
    [InlineData("[1,2][0.5]", ExpressionErrorCodes.TypeError)]
    [InlineData("[1,2][null]", ExpressionErrorCodes.TypeError)]
    [InlineData("5[0]", ExpressionErrorCodes.TypeError)]
    [InlineData("'s'[0]", ExpressionErrorCodes.TypeError)]
    [InlineData("true[0]", ExpressionErrorCodes.TypeError)]
    [InlineData("{a:1}[0]", ExpressionErrorCodes.TypeError)]
    // out-of-range / null-index are terminal index_out_of_range (reproduces C# Split(...)[1] throw)
    [InlineData("[1,2][5]", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("[1,2][-1]", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("[][0]", ExpressionErrorCodes.IndexOutOfRange)]
    // unknown identifier at eval (the parser cannot know var names)
    [InlineData("undefinedVar", ExpressionErrorCodes.UnknownIdentifier)]
    [InlineData("foo + 1", ExpressionErrorCodes.UnknownIdentifier)]
    public async Task Terminal_failures(string source, string expectedCode) =>
        (await Xp.EvalErrorAsync(source)).Code.ShouldBe(expectedCode);

    [Fact]
    public async Task Handle_is_an_invalid_operand_and_is_named_in_the_message()
    {
        var scope = new FakeScope().With("h", new FakeHandle());
        var error = await Xp.EvalErrorAsync("h - 1", scope);
        error.Code.ShouldBe(ExpressionErrorCodes.TypeError);
        error.Message.ShouldContain("handle");
    }

    [Fact]
    public async Task Handle_equality_is_a_type_error()
    {
        var scope = new FakeScope().With("h", new FakeHandle());
        (await Xp.EvalErrorAsync("h == h", scope)).Code.ShouldBe(ExpressionErrorCodes.TypeError);
    }

    [Fact]
    public async Task Negating_a_handle_is_a_type_error()
    {
        var scope = new FakeScope().With("h", new FakeHandle());
        (await Xp.EvalErrorAsync("-h", scope)).Code.ShouldBe(ExpressionErrorCodes.TypeError);
    }

    [Fact]
    public async Task Null_safe_equality_against_a_handle_is_false_not_an_error()
    {
        var scope = new FakeScope().With("h", new FakeHandle());
        (await Xp.EvalAsync("h == null", scope)).ShouldBe(false);
        (await Xp.EvalAsync("null == h", scope)).ShouldBe(false);
    }

    [Fact]
    public async Task Indexing_into_null_is_terminal_index_out_of_range()
    {
        var scope = new FakeScope().With("n", null);
        (await Xp.EvalErrorAsync("n[0]", scope)).Code.ShouldBe(ExpressionErrorCodes.IndexOutOfRange);
    }
}
