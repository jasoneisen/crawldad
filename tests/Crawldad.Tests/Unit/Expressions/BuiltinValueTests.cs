using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

public class BuiltinValueTests
{
    [Theory]
    // isNullOrWhitespace — null-propagation folds null → true
    [InlineData("isNullOrWhitespace(null)", true)]
    [InlineData("isNullOrWhitespace('   ')", true)]
    [InlineData("isNullOrWhitespace('x')", false)]
    // trim / lower / upper null-propagate on the primary
    [InlineData("trim('  hi  ')", "hi")]
    [InlineData("trim(null)", null)]
    [InlineData("lower('AbC')", "abc")]
    [InlineData("lower(null)", null)]
    [InlineData("upper('aBc')", "ABC")]
    [InlineData("upper(null)", null)]
    // string(x) — null → "" (not null), the value conversion
    [InlineData("string(null)", "")]
    [InlineData("string(true)", "true")]
    [InlineData("string(false)", "false")]
    [InlineData("string(5)", "5")]
    [InlineData("string(1.5)", "1.5")]
    [InlineData("string('x')", "x")]
    // length — string or array; null → null; never touches the DOM
    [InlineData("length('hello')", 5L)]
    [InlineData("length('')", 0L)]
    [InlineData("length([1,2,3])", 3L)]
    [InlineData("length([])", 0L)]
    [InlineData("length(null)", null)]
    // startsWith / contains null-propagate on the primary
    [InlineData("startsWith('hello','he')", true)]
    [InlineData("startsWith('hello','x')", false)]
    [InlineData("startsWith(null,'x')", null)]
    [InlineData("contains('hello','ell')", true)]
    [InlineData("contains('hello','zz')", false)]
    [InlineData("contains(null,'x')", null)]
    // toInt / isInt (invariant integer; whitespace + sign allowed)
    [InlineData("toInt('123')", 123L)]
    [InlineData("toInt('  12 ')", 12L)]
    [InlineData("toInt('-5')", -5L)]
    [InlineData("isInt('12')", true)]
    [InlineData("isInt('x')", false)]
    [InlineData("isInt(null)", false)]
    [InlineData("isInt(5)", false)]
    // coalesce (≥2 args): first non-null
    [InlineData("coalesce(null, 'x')", "x")]
    [InlineData("coalesce('a', 'b')", "a")]
    [InlineData("coalesce(null, null)", null)]
    [InlineData("coalesce(null, null, 3)", 3L)]
    // count over in-memory containers is a size (no DOM)
    [InlineData("count([1,2,3])", 3L)]
    [InlineData("count({a:1, b:2})", 2L)]
    // URL builtins
    [InlineData("urlScheme('https://aca-prod.accela.com/x')", "https")]
    [InlineData("urlHost('https://aca-prod.accela.com/x')", "aca-prod.accela.com")]
    [InlineData("urlPath('https://h/a/b')", "/a/b")]
    public async Task Pure_builtins(string source, object? expected) =>
        (await Xp.EvalAsync(source)).ShouldBe(expected);

    [Theory]
    [InlineData("isNullOrWhitespace(5)", ExpressionErrorCodes.TypeError)]
    [InlineData("trim(5)", ExpressionErrorCodes.TypeError)]
    [InlineData("lower(5)", ExpressionErrorCodes.TypeError)]
    [InlineData("upper(5)", ExpressionErrorCodes.TypeError)]
    [InlineData("length(5)", ExpressionErrorCodes.TypeError)]
    [InlineData("length({a:1})", ExpressionErrorCodes.TypeError)]
    [InlineData("startsWith(5,'x')", ExpressionErrorCodes.TypeError)]
    [InlineData("startsWith('h',5)", ExpressionErrorCodes.TypeError)]
    [InlineData("contains(5,'x')", ExpressionErrorCodes.TypeError)]
    [InlineData("contains('h',5)", ExpressionErrorCodes.TypeError)]
    [InlineData("toInt('abc')", ExpressionErrorCodes.IntConversionFailed)]
    [InlineData("toInt(null)", ExpressionErrorCodes.IntConversionFailed)]
    [InlineData("toInt(5)", ExpressionErrorCodes.IntConversionFailed)]
    [InlineData("count(null)", ExpressionErrorCodes.TypeError)]
    [InlineData("count(5)", ExpressionErrorCodes.TypeError)]
    [InlineData("count(true)", ExpressionErrorCodes.TypeError)]
    [InlineData("count(1.5)", ExpressionErrorCodes.TypeError)]
    [InlineData("urlScheme('not a url')", ExpressionErrorCodes.InvalidUrl)]
    [InlineData("urlScheme(null)", ExpressionErrorCodes.InvalidUrl)]
    [InlineData("urlPath(5)", ExpressionErrorCodes.InvalidUrl)]
    public async Task Builtin_terminal_failures(string source, string expectedCode) =>
        (await Xp.EvalErrorAsync(source)).Code.ShouldBe(expectedCode);

    [Fact]
    public async Task Coalesce_short_circuits_on_the_first_non_null()
    {
        // The second argument would be a terminal index failure if evaluated.
        (await Xp.EvalAsync("coalesce('a', [1][9])")).ShouldBe("a");
    }

    [Fact]
    public async Task PageUrl_reads_the_scope()
    {
        var scope = new FakeScope(pageUrl: "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx");
        (await Xp.EvalAsync("pageUrl()", scope)).ShouldBe("https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx");
        (await Xp.EvalAsync("urlScheme(pageUrl())", scope)).ShouldBe("https");
        (await Xp.EvalAsync("urlHost(pageUrl())", scope)).ShouldBe("aca-prod.accela.com");
        (await Xp.EvalAsync("urlPath(pageUrl())", scope)).ShouldBe("/LJCMG/Cap/CapHome.aspx");
    }
}
