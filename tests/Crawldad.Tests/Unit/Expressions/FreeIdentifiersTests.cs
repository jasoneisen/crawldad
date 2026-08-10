using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

/// <summary>The free-identifier walk that backs save-time defined-before-use: every AST node shape contributes exactly
/// the bare variable names it reads, function names never appear, and binding builtins exclude their bound variable
/// (including under shadowing). Templates union the identifiers of their <c>${…}</c> interpolations.</summary>
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

    // ----- the input.<key> walk backing the secretRef guardrail -----

    private static IReadOnlyList<string> Members(string source) =>
        [.. CrawldadExpression.Parse(source).InputMemberReferences().OrderBy(s => s, StringComparer.Ordinal)];

    private static IReadOnlyList<string> MembersTmpl(string source) =>
        [.. CrawldadTemplate.Parse(source).InputMemberReferences().OrderBy(s => s, StringComparer.Ordinal)];

    [Fact]
    public void Every_node_shape_is_walked_for_input_member_references()
    {
        Members("42").ShouldBeEmpty();                                    // literal
        Members("input").ShouldBeEmpty();                                 // a bare input is not a keyed reference
        Members("other").ShouldBeEmpty();                                 // identifier
        Members("input.a").ShouldBe(["a"]);                               // member → the top-level input key
        Members("input.a.b").ShouldBe(["a"]);                             // nested member → still the top-level key
        Members("other.a").ShouldBeEmpty();                               // member on a non-input target
        Members("input['a']").ShouldBe(["a"]);                            // index with a string literal
        Members("input[k]").ShouldBeEmpty();                              // a computed index is not statically a key
        Members("other['a']").ShouldBeEmpty();                            // index on a non-input target
        Members("trim(input.a)").ShouldBe(["a"]);                         // call argument
        Members("input.a + input.b").ShouldBe(["a", "b"]);                // binary
        Members("input.a && input.b").ShouldBe(["a", "b"]);               // and
        Members("input.a || input.b").ShouldBe(["a", "b"]);               // or
        Members("input.a ? input.b : input.c").ShouldBe(["a", "b", "c"]); // ternary
        Members("!input.a").ShouldBe(["a"]);                              // not
        Members("-input.a").ShouldBe(["a"]);                              // negate
        Members("[input.a, input.b]").ShouldBe(["a", "b"]);               // array
        Members("{ k: input.a }").ShouldBe(["a"]);                        // object — the key is not a reference
        Members("map(input.xs, x, x + input.y)").ShouldBe(["xs", "y"]);   // binding builtin: source + body
        Members("map(ys, input, input.a)").ShouldBeEmpty();               // a binding named `input` shadows the run input in the body
    }

    [Fact]
    public void Templates_are_walked_for_input_member_references()
    {
        MembersTmpl("plain text").ShouldBeEmpty();
        MembersTmpl("a${input.x}b${input.y}c").ShouldBe(["x", "y"]);
    }

    [Theory]
    [InlineData("input.pw", "pw")]
    [InlineData("input['pw']", "pw")]
    public void A_bare_input_reference_is_recognised_for_fill_secret(string source, string expected)
    {
        CrawldadExpression.Parse(source).TryGetInputMemberReference(out var name).ShouldBeTrue();
        name.ShouldBe(expected);
    }

    [Theory]
    [InlineData("input.pw + 'x'")]  // an operator — not a bare reference
    [InlineData("pw")]              // a bare identifier
    [InlineData("input")]           // bare input, no member
    [InlineData("input[i]")]        // a computed index
    [InlineData("other.pw")]        // a member on a non-input target
    [InlineData("trim(input.pw)")]  // a call
    public void A_non_bare_input_reference_is_rejected_for_fill_secret(string source)
    {
        CrawldadExpression.Parse(source).TryGetInputMemberReference(out var name).ShouldBeFalse();
        name.ShouldBeNull();
    }
}
