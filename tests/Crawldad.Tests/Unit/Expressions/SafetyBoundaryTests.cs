using Crawldad.Api.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

/// <summary>The safety gate: "no expression can be authored that loops, recurses, calls fs, or evals." These are the
/// negative tests — static parser rejections (unknown builtins, wrong arity, non-identifier binding slots, absent
/// function-definition syntax), runtime termination guarantees (bounded iteration, depth-capped nesting), and the regex time/size guards.</summary>
public class SafetyBoundaryTests
{
    [Theory]
    // There is no eval / module loader / fs / import / spawn — these are just unknown functions, rejected before
    // execution. (Crawldad's own `require(...)` is unrelated: a safe extraction-severity wrapper, not a module loader —
    // see SelectorMissTests — so it is deliberately absent here.)
    [InlineData("eval('1+1')")]
    [InlineData("fs('/etc/passwd')")]
    [InlineData("readFile('x')")]
    [InlineData("import('m')")]
    [InlineData("exec('sh')")]
    [InlineData("spawn('sh')")]
    [InlineData("system('rm')")]
    [InlineData("fetch('http://x')")]
    public void No_escape_hatch_builtins_exist(string source) =>
        Xp.ParseError(source).Code.ShouldBe(ExpressionErrorCodes.UnknownFunction);

    [Theory]
    // Wrong arity on every new builtin is a static rejection (below-min and above-max forms).
    [InlineData("replace('a','b')")]
    [InlineData("replaceRegex('a','b')")]
    [InlineData("split('a')")]
    [InlineData("substring('a')")]
    [InlineData("substring('a','b','c','d')")]
    [InlineData("substringAfterLast('a')")]
    [InlineData("endsWith('a')")]
    [InlineData("indexOf('a')")]
    [InlineData("lastIndexOf('a')")]
    [InlineData("matches('a')")]
    [InlineData("equalsIgnoreCase('a')")]
    [InlineData("join([1])")]
    [InlineData("first([1],[2])")]
    [InlineData("last()")]
    [InlineData("nth([1])")]
    [InlineData("slice([1])")]
    [InlineData("slice([1],2,3,4)")]
    [InlineData("reverse()")]
    [InlineData("distinct()")]
    [InlineData("min()")]
    [InlineData("max([1],[2])")]
    [InlineData("keys()")]
    [InlineData("get({})")]
    [InlineData("resolveUrl('a')")]
    // Binding builtins are the fixed 3-argument form; any other count is wrong arity.
    [InlineData("filter([1], v)")]
    [InlineData("map([1])")]
    [InlineData("any([1], v, p, q)")]
    [InlineData("all([1])")]
    [InlineData("sortBy([1], v)")]
    public void Wrong_arity_is_rejected_at_parse_time(string source) =>
        Xp.ParseError(source).Code.ShouldBe(ExpressionErrorCodes.WrongArity);

    [Theory]
    // A binding slot must be a bare identifier — no computation, literal, or member access can be smuggled in.
    [InlineData("filter(xs, 1 + 1, n < 3)")]
    [InlineData("map(xs, 'v', v)")]
    [InlineData("any(xs, 5, true)")]
    [InlineData("all(xs, foo.bar, true)")]
    [InlineData("sortBy(xs, x[0], x)")]
    public void A_non_identifier_binding_slot_is_a_syntax_error(string source) =>
        Xp.ParseError(source).Code.ShouldBe(ExpressionErrorCodes.SyntaxError);

    [Theory]
    // The grammar has no function-definition, lambda, or assignment form — every such attempt fails to parse, so no
    // expression can define a recursive/user function or mutate state (mutation is the structural set/push).
    [InlineData("n => n + 1", ExpressionErrorCodes.SyntaxError)]     // no lambda arrow ('=' demands '==')
    [InlineData("x = 5", ExpressionErrorCodes.SyntaxError)]          // no assignment
    [InlineData("def add(a, b)", ExpressionErrorCodes.SyntaxError)]  // 'def' is just an identifier; 'add' is trailing
    [InlineData("function(x)", ExpressionErrorCodes.UnknownFunction)] // no user functions to call
    public void No_function_definition_or_assignment_form_parses(string source, string expectedCode) =>
        Xp.ParseError(source).Code.ShouldBe(expectedCode);

    [Fact]
    public async Task Binding_iteration_over_a_large_bounded_list_terminates_linearly()
    {
        // Iteration is a bounded walk of the (already-finite) input list — not recursion, not an open loop. A big list
        // completes in linear time; the test finishing is the termination proof.
        var big = new List<object?>();
        for (var i = 0; i < 20000; i++)
        {
            big.Add((long)i);
        }

        var scope = new FakeScope().With("xs", big);
        (await Xp.EvalAsync("length(filter(xs, n, n < 5000))", scope)).ShouldBe(5000L);
        (await Xp.EvalAsync("last(map(xs, n, n * 2))", scope)).ShouldBe(39998L);
        (await Xp.EvalAsync("any(xs, n, n == 19999)", scope)).ShouldBe(true);
    }

    [Fact]
    public void Deeply_nested_binding_builtins_hit_the_depth_cap()
    {
        // Nesting binding builtins past the parser's depth limit is a terminal parse failure — the same cap that stops
        // pathological parenthesis nesting also bounds nested filter/map, so runtime recursion depth is bounded.
        var source = "[1]";
        for (var i = 0; i < 100; i++)
        {
            source = $"map({source}, a, a)";
        }

        Xp.ParseError(source).Code.ShouldBe(ExpressionErrorCodes.ExpressionTooDeep);
    }
}
