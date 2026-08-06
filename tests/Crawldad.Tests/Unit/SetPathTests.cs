using Crawldad.Web.Features.Runs.Interpreter;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The <c>set.path</c> grammar parser (§7.4): dotted literal segments and <c>[${Expr}]</c> computed segments,
/// composable. Rendering/upsert/type-error behaviour is covered through the interpreter (<see cref="InterpreterNodesTests"/>);
/// these pin the parse itself, including the closing-bracket scan that skips single-quoted strings.
/// </summary>
public class SetPathTests
{
    [Fact]
    public void A_bare_name_is_one_literal_segment()
    {
        var segments = SetPath.Parse("title");
        segments.Count.ShouldBe(1);
        segments[0].ShouldBeOfType<LiteralSegment>().Name.ShouldBe("title");
    }

    [Fact]
    public void A_bracket_run_is_one_computed_segment()
    {
        SetPath.Parse("[${indent}]").ShouldHaveSingleItem().ShouldBeOfType<ComputedSegment>();
    }

    [Fact]
    public void Dotted_and_computed_segments_compose()
    {
        var segments = SetPath.Parse("a.b[${k}]");
        segments.Count.ShouldBe(3);
        segments[0].ShouldBeOfType<LiteralSegment>().Name.ShouldBe("a");
        segments[1].ShouldBeOfType<LiteralSegment>().Name.ShouldBe("b");
        segments[2].ShouldBeOfType<ComputedSegment>();
    }

    [Fact]
    public void A_quoted_string_inside_the_brackets_does_not_end_the_segment()
    {
        // The `']'`-bearing key expression from B.2 must not have its closing bracket found inside the string literal.
        SetPath.Parse("[${endsWith(h,':') ? substring(h,0,length(h)-1) : h}]")
            .ShouldHaveSingleItem().ShouldBeOfType<ComputedSegment>();
    }

    [Fact]
    public void An_unterminated_bracket_is_malformed()
    {
        // The unterminated quoted string runs the closing-bracket scan to the end, which then reports the malformed path.
        Should.Throw<InterpreterException>(() => SetPath.Parse("[${'x}"))
            .Code.ShouldBe(InterpreterErrorCodes.MalformedNode);
    }
}
