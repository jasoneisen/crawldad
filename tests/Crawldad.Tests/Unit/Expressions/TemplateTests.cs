using Crawldad.Api.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

public class TemplateTests
{
    private static async Task<string> RenderAsync(string source, IEvalScope? scope = null) =>
        await CrawldadTemplate.Parse(source).RenderAsync(scope ?? new FakeScope());

    [Theory]
    [InlineData("hello world", "hello world")]
    [InlineData("", "")]
    [InlineData("^abc$", "^abc$")] // a lone '$' with no '{' is literal
    [InlineData("100%", "100%")]
    public async Task Constant_templates_render_verbatim(string source, string expected) =>
        (await RenderAsync(source)).ShouldBe(expected);

    [Theory]
    [InlineData("n=${1 + 2}", "n=3")]
    [InlineData("${1}", "1")]
    [InlineData("a${1}b${2}c", "a1b2c")]
    [InlineData("${true}", "true")]
    [InlineData("${1.5}", "1.5")]
    [InlineData("tr:nth-child(${1 + 1})", "tr:nth-child(2)")]
    [InlineData("^${'3'}$", "^3$")]
    // interpolations honour nested braces and quoted strings inside ${...}
    [InlineData("v=${ {a:1}['a'] }", "v=1")]
    [InlineData("${ '}' }", "}")]
    [InlineData(@"${ 'a\'b' }", "a'b")]
    public async Task Interpolations_render(string source, string expected) =>
        (await RenderAsync(source)).ShouldBe(expected);

    [Fact]
    public async Task Null_interpolation_contributes_empty_string()
    {
        var scope = new FakeScope().With("nv", null);
        (await RenderAsync("x=${nv}", scope)).ShouldBe("x=");
    }

    [Fact]
    public async Task Interpolation_reads_scope_and_dom()
    {
        var dom = new FakeDom { OnText = static (_, _) => "42" };
        var scope = new FakeScope(dom).With("i", 2L);
        (await RenderAsync("row-${i + 1}-${text('sel')}", scope)).ShouldBe("row-3-42");
    }

    [Theory]
    [InlineData("${ x")]        // unterminated interpolation
    [InlineData("${ 'abc")]     // unterminated string inside an interpolation
    public void Unterminated_interpolation_is_a_syntax_error(string source) =>
        Should.Throw<ExpressionParseException>(() => CrawldadTemplate.Parse(source))
            .Code.ShouldBe(ExpressionErrorCodes.SyntaxError);

    [Fact]
    public void A_bad_builtin_inside_an_interpolation_is_rejected_at_parse_time() =>
        Should.Throw<ExpressionParseException>(() => CrawldadTemplate.Parse("x=${ foo(1) }"))
            .Code.ShouldBe(ExpressionErrorCodes.UnknownFunction);
}
