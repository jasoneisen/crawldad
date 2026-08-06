using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

/// <summary>
/// The free-identifier walk (§12) that backs save-time defined-before-use: every AST node shape contributes exactly
/// the bare variable names it reads, function names never appear, and binding builtins exclude their bound variable
/// (including under shadowing). Templates union the identifiers of their <c>${…}</c> interpolations.
/// </summary>
public class FreeIdentifiersTests
{
    private static IReadOnlyList<string> Free(string source) =>
        [.. CrawldadExpression.Parse(source).FreeIdentifiers().OrderBy(s => s, StringComparer.Ordinal)];

    private static IReadOnlyList<string> FreeTmpl(string source) =>
        [.. CrawldadTemplate.Parse(source).FreeIdentifiers().OrderBy(s => s, StringComparer.Ordinal)];

    [Fact]
    public void Every_node_shape_contributes_its_reads()
    {
        Free("42").ShouldBeEmpty();                       // literal
        Free("a").ShouldBe(["a"]);                        // identifier
        Free("a.b").ShouldBe(["a"]);                      // member — the member name is not a reference
        Free("a[b]").ShouldBe(["a", "b"]);                // index — both target and index
        Free("trim(a)").ShouldBe(["a"]);                  // call — the function name is not a reference
        Free("a + b").ShouldBe(["a", "b"]);               // binary
        Free("a && b").ShouldBe(["a", "b"]);              // and
        Free("a || b").ShouldBe(["a", "b"]);              // or
        Free("a ? b : c").ShouldBe(["a", "b", "c"]);      // ternary
        Free("!a").ShouldBe(["a"]);                       // not
        Free("-a").ShouldBe(["a"]);                       // negate
        Free("[a, b]").ShouldBe(["a", "b"]);              // array
        Free("{ k: a }").ShouldBe(["a"]);                 // object — the key is not a reference
    }

    [Fact]
    public void Binding_builtins_exclude_their_binding_and_handle_shadowing()
    {
        Free("map(xs, x, x + y)").ShouldBe(["xs", "y"]);                          // x is bound; xs and y are free
        Free("filter(map(keys(m), k, toInt(k)), n, n < i)").ShouldBe(["i", "m"]); // nested bindings, both excluded
        Free("map(xs, x, map(x, x, x))").ShouldBe(["xs"]);                        // inner x shadows outer x
    }

    [Fact]
    public void Templates_union_their_interpolations()
    {
        FreeTmpl("just literal text").ShouldBeEmpty();     // no interpolation
        FreeTmpl("a${x}b${y}c").ShouldBe(["x", "y"]);      // literal + interpolation segments interleaved
    }
}
