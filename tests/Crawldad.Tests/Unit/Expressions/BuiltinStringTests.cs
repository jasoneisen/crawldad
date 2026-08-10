using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

/// <summary>The string surface: ordinal semantics, primary-argument null-propagation, and the reference's
/// out-of-range throws reproduced as terminal failures.</summary>
public class BuiltinStringTests
{
    [Theory]
    // replace — ordinal, all occurrences (C# string.Replace)
    [InlineData("replace('a*b*c', '*', '')", "abc")]
    [InlineData("replace('120px', 'px', '')", "120")]
    [InlineData("replace('Due on 5/1', 'Due on ', '')", "5/1")]
    [InlineData("replace('none', 'x', 'y')", "none")]
    [InlineData("replace(null, '*', '')", null)]
    // replaceRegex — C# Regex.Replace through the guarded factory
    [InlineData("replaceRegex('a1b2c3', '[0-9]', '#')", "a#b#c#")]
    [InlineData("replaceRegex('foo42', '([a-z]+)([0-9]+)', '$2$1')", "42foo")]
    [InlineData("replaceRegex(null, 'x', 'y')", null)]
    // split — keeps empty entries; null primary → null
    [InlineData("length(split('a,b,c', ','))", 3L)]
    [InlineData("length(split('a,,c', ','))", 3L)]
    [InlineData("length(split(',a,', ','))", 3L)]
    [InlineData("split('a,b,c', ',')[1]", "b")]
    [InlineData("length(split('no-delimiter', ','))", 1L)]
    [InlineData("length(split('one<br>two<br>three', '<br>'))", 3L)]
    [InlineData("split(null, ',')", null)]
    // substring — (start, endExclusive), NOT (start, length)
    [InlineData("substring('hello', 2)", "llo")]
    [InlineData("substring('hello', 0, length('hello') - 1)", "hell")]
    [InlineData("substring('name', 1, 2)", "a")]
    [InlineData("substring('hello', 5)", "")]
    [InlineData("substring('hello', 0, 0)", "")]
    [InlineData("substring('hello', 1.0, 2.0)", "e")]
    [InlineData("substring(null, 1)", null)]
    // substringAfterLast — whole string when sep absent
    [InlineData("substringAfterLast('a.b.txt', '.')", "txt")]
    [InlineData("substringAfterLast('report.pdf', '.')", "pdf")]
    [InlineData("substringAfterLast('noext', '.')", "noext")]
    [InlineData("substringAfterLast('', '.')", "")]
    [InlineData("substringAfterLast(null, '.')", null)]
    // endsWith
    [InlineData("endsWith('heading:', ':')", true)]
    [InlineData("endsWith('heading', ':')", false)]
    [InlineData("endsWith(null, ':')", null)]
    // indexOf / lastIndexOf — ordinal, -1 when absent
    [InlineData("indexOf('hello', 'l')", 2L)]
    [InlineData("indexOf('hello', 'z')", -1L)]
    [InlineData("indexOf(null, 'x')", null)]
    [InlineData("lastIndexOf('hello', 'l')", 3L)]
    [InlineData("lastIndexOf('hello', 'z')", -1L)]
    [InlineData("lastIndexOf(null, 'x')", null)]
    // matches — Regex.IsMatch, guarded; null primary → null
    [InlineData("matches('12', '^[0-9]+$')", true)]
    [InlineData("matches('x12', '^[0-9]+$')", false)]
    [InlineData("matches(null, '^x$')", null)]
    // equalsIgnoreCase — OrdinalIgnoreCase, null-safe
    [InlineData("equalsIgnoreCase('VIOLATIONS', 'violations')", true)]
    [InlineData("equalsIgnoreCase('a', 'b')", false)]
    [InlineData("equalsIgnoreCase(null, null)", true)]
    [InlineData("equalsIgnoreCase(null, 'x')", false)]
    [InlineData("equalsIgnoreCase('x', null)", false)]
    // join — string(x) conversion per element (null → "")
    [InlineData("join(['a', 'b', 'c'], ',')", "a,b,c")]
    [InlineData("join(['a', null, 'b'], ',')", "a,,b")]
    [InlineData("join([1, 2, 3], '-')", "1-2-3")]
    [InlineData("join([], ',')", "")]
    [InlineData("join(null, ',')", null)]
    public async Task String_builtins(string source, object? expected) =>
        (await Xp.EvalAsync(source)).ShouldBe(expected);

    [Theory]
    // replace type errors + the empty-search terminal (C# string.Replace throws on empty oldValue)
    [InlineData("replace(5, '*', '')", ExpressionErrorCodes.TypeError)]
    [InlineData("replace('a', 5, '')", ExpressionErrorCodes.TypeError)]
    [InlineData("replace('a', '*', 5)", ExpressionErrorCodes.TypeError)]
    [InlineData("replace('a', '', 'x')", ExpressionErrorCodes.TypeError)]
    // replaceRegex type errors
    [InlineData("replaceRegex(5, 'x', 'y')", ExpressionErrorCodes.TypeError)]
    [InlineData("replaceRegex('a', 5, 'y')", ExpressionErrorCodes.TypeError)]
    [InlineData("replaceRegex('a', 'x', 5)", ExpressionErrorCodes.TypeError)]
    // split — separator must be a string; primary must be a string
    [InlineData("split('a', 5)", ExpressionErrorCodes.TypeError)]
    [InlineData("split('a', null)", ExpressionErrorCodes.TypeError)]
    [InlineData("split(5, ',')", ExpressionErrorCodes.TypeError)]
    // substring — non-string primary, non-integer index, and out-of-range start/end
    [InlineData("substring(5, 1)", ExpressionErrorCodes.TypeError)]
    [InlineData("substring('abc', 'x')", ExpressionErrorCodes.TypeError)]
    [InlineData("substring('abc', 1.5)", ExpressionErrorCodes.TypeError)]
    [InlineData("substring('hello', -1)", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("substring('hello', 6)", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("substring('hello', 2, 1)", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("substring('hello', 0, 6)", ExpressionErrorCodes.IndexOutOfRange)]
    // substringAfterLast — empty separator reproduces the C# throw (LastIndexOf('') == length)
    [InlineData("substringAfterLast('abc', '')", ExpressionErrorCodes.IndexOutOfRange)]
    [InlineData("substringAfterLast('abc', 5)", ExpressionErrorCodes.TypeError)]
    [InlineData("substringAfterLast(5, '.')", ExpressionErrorCodes.TypeError)]
    // endsWith / indexOf / lastIndexOf / matches — string arguments required
    [InlineData("endsWith(5, ':')", ExpressionErrorCodes.TypeError)]
    [InlineData("endsWith('a', 5)", ExpressionErrorCodes.TypeError)]
    [InlineData("indexOf('a', 5)", ExpressionErrorCodes.TypeError)]
    [InlineData("lastIndexOf(5, 'a')", ExpressionErrorCodes.TypeError)]
    [InlineData("matches('a', 5)", ExpressionErrorCodes.TypeError)]
    // equalsIgnoreCase — non-null non-string operands
    [InlineData("equalsIgnoreCase(5, 'a')", ExpressionErrorCodes.TypeError)]
    [InlineData("equalsIgnoreCase('a', 5)", ExpressionErrorCodes.TypeError)]
    // join — non-array list, non-string separator, non-stringable element
    [InlineData("join('notarray', ',')", ExpressionErrorCodes.TypeError)]
    [InlineData("join(['a'], 5)", ExpressionErrorCodes.TypeError)]
    [InlineData("join([[1]], ',')", ExpressionErrorCodes.TypeError)]
    public async Task String_builtin_terminal_failures(string source, string expectedCode) =>
        (await Xp.EvalErrorAsync(source)).Code.ShouldBe(expectedCode);

    [Fact]
    public async Task Split_preserves_leading_and_trailing_empty_entries()
    {
        var parts = (await Xp.EvalAsync("split(',a,', ',')")).ShouldBeAssignableTo<List<object?>>()!;
        parts.ShouldBe(["", "a", ""]);
    }
}
