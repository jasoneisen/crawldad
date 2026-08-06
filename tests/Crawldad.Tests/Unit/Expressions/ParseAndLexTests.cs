using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

public class ParseAndLexTests
{
    [Theory]
    // string escapes the payloads use
    [InlineData(@"'\n'", "\n")]
    [InlineData(@"'\t'", "\t")]
    [InlineData(@"'\r'", "\r")]
    [InlineData(@"'\\'", "\\")]
    [InlineData(@"'\''", "'")]
    [InlineData("'plain'", "plain")]
    public async Task String_escapes(string source, string expected) =>
        (await Xp.EvalAsync(source)).ShouldBe(expected);

    [Theory]
    [InlineData("foo(1)", ExpressionErrorCodes.UnknownFunction)]
    [InlineData("bar()", ExpressionErrorCodes.UnknownFunction)]
    [InlineData("trim()", ExpressionErrorCodes.WrongArity)]
    [InlineData("trim('a','b')", ExpressionErrorCodes.WrongArity)]
    [InlineData("coalesce('a')", ExpressionErrorCodes.WrongArity)]
    [InlineData("pageUrl(1)", ExpressionErrorCodes.WrongArity)]
    [InlineData("attr('a')", ExpressionErrorCodes.WrongArity)]
    [InlineData("attr('a','b','c','d')", ExpressionErrorCodes.WrongArity)]
    // syntax
    [InlineData("1 +", ExpressionErrorCodes.SyntaxError)]
    [InlineData("", ExpressionErrorCodes.SyntaxError)]
    [InlineData(")", ExpressionErrorCodes.SyntaxError)]
    [InlineData("(1", ExpressionErrorCodes.SyntaxError)]
    [InlineData("[1,2", ExpressionErrorCodes.SyntaxError)]
    [InlineData("{a:1", ExpressionErrorCodes.SyntaxError)]
    [InlineData("{a 1}", ExpressionErrorCodes.SyntaxError)]
    [InlineData("{1: 2}", ExpressionErrorCodes.SyntaxError)]
    [InlineData("a.5", ExpressionErrorCodes.SyntaxError)]
    [InlineData("a[1", ExpressionErrorCodes.SyntaxError)]
    [InlineData("trim('a'", ExpressionErrorCodes.SyntaxError)]
    [InlineData("true ? 1", ExpressionErrorCodes.SyntaxError)]
    // trailing tokens (covers the token-describer arms)
    [InlineData("1 2", ExpressionErrorCodes.SyntaxError)]
    [InlineData("1 foo", ExpressionErrorCodes.SyntaxError)]
    [InlineData("1 'x'", ExpressionErrorCodes.SyntaxError)]
    [InlineData("1 )", ExpressionErrorCodes.SyntaxError)]
    // lexer
    [InlineData("@", ExpressionErrorCodes.SyntaxError)]
    [InlineData("=", ExpressionErrorCodes.SyntaxError)]
    [InlineData("&", ExpressionErrorCodes.SyntaxError)]
    [InlineData("|", ExpressionErrorCodes.SyntaxError)]
    [InlineData("'unterminated", ExpressionErrorCodes.SyntaxError)]
    [InlineData(@"'bad\q'", ExpressionErrorCodes.SyntaxError)]
    [InlineData(@"'trailing-backslash\", ExpressionErrorCodes.SyntaxError)]
    [InlineData("1 <", ExpressionErrorCodes.SyntaxError)]
    [InlineData("99999999999999999999999", ExpressionErrorCodes.SyntaxError)]
    public void Parse_failures(string source, string expectedCode) =>
        Xp.ParseError(source).Code.ShouldBe(expectedCode);

    [Fact]
    public void Unknown_function_position_points_at_the_name()
    {
        var error = Xp.ParseError("  foo(1)");
        error.Code.ShouldBe(ExpressionErrorCodes.UnknownFunction);
        error.Position.ShouldBe(2);
    }

    [Fact]
    public void Wrong_arity_message_describes_the_expected_window()
    {
        Xp.ParseError("attr('a')").Message.ShouldContain("2 to 3");
        Xp.ParseError("coalesce('a')").Message.ShouldContain("at least 2");
        Xp.ParseError("trim('a','b')").Message.ShouldContain("expects 1");
    }

    [Fact]
    public void Deeply_nested_parentheses_hit_the_depth_cap()
    {
        var source = new string('(', 100) + "1" + new string(')', 100);
        Xp.ParseError(source).Code.ShouldBe(ExpressionErrorCodes.ExpressionTooDeep);
    }

    [Fact]
    public void Deeply_nested_unary_operators_hit_the_depth_cap()
    {
        var source = new string('!', 100) + "true";
        Xp.ParseError(source).Code.ShouldBe(ExpressionErrorCodes.ExpressionTooDeep);
    }

    [Fact]
    public async Task Parsed_expression_exposes_its_source()
    {
        var expression = CrawldadExpression.Parse("1 + 2");
        expression.Source.ShouldBe("1 + 2");
        (await expression.EvaluateAsync(new FakeScope())).ShouldBe(3L);
    }
}
