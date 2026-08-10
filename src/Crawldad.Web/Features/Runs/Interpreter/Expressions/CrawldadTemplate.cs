using System.Text;

namespace Crawldad.Web.Features.Runs.Interpreter.Expressions;

/// <summary>A <c>Tmpl</c> field: a string with <c>${Expr}</c> interpolations, rendered by concatenating literal runs
/// with each interpolation's <c>string(x)</c>-converted value (null → <c>""</c>). A template with no <c>${}</c> is a
/// pure literal; a bad builtin or arity inside <c>${…}</c> is rejected at parse time.</summary>
public sealed class CrawldadTemplate
{
    private readonly string? _constant;
    private readonly IReadOnlyList<Segment>? _segments;

    private CrawldadTemplate(string constant) => _constant = constant;

    private CrawldadTemplate(IReadOnlyList<Segment> segments) => _segments = segments;

    /// <summary>Parses <paramref name="source"/> into a renderable template. Interpolations honour nested braces and
    /// single-quoted strings, so <c>${ {a:1}['a'] }</c> and <c>${ '}' }</c> parse correctly. An unterminated
    /// <c>${…}</c> or malformed expression raises <see cref="ExpressionParseException"/>.</summary>
    public static CrawldadTemplate Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var segments = new List<Segment>();
        var literal = new StringBuilder();
        var hasInterpolation = false;
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];
            if (c == '$' && i + 1 < source.Length && source[i + 1] == '{')
            {
                if (literal.Length > 0)
                {
                    segments.Add(new Segment(literal.ToString(), null));
                    literal.Clear();
                }

                var start = i + 2;
                var end = FindInterpolationEnd(source, start);
                segments.Add(new Segment(null, CrawldadExpression.Parse(source[start..end])));
                hasInterpolation = true;
                i = end + 1;
            }
            else
            {
                literal.Append(c);
                i++;
            }
        }

        if (!hasInterpolation)
        {
            return new CrawldadTemplate(source);
        }

        if (literal.Length > 0)
        {
            segments.Add(new Segment(literal.ToString(), null));
        }

        return new CrawldadTemplate(segments);
    }

    /// <summary>Renders the template against <paramref name="scope"/>, evaluating each interpolation and concatenating.</summary>
    public ValueTask<string> RenderAsync(IEvalScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return _segments is null ? new ValueTask<string>(_constant!) : RenderSegmentsAsync(scope, ct);
    }

    /// <summary>The free variable identifiers read across this template's <c>${…}</c> interpolations (a literal
    /// template reads none). Backs save-time defined-before-use validation; a pure static walk.</summary>
    public IReadOnlySet<string> FreeIdentifiers()
    {
        var into = new HashSet<string>(StringComparer.Ordinal);
        if (_segments is not null)
        {
            foreach (var segment in _segments)
            {
                if (segment.Expression is not null)
                {
                    into.UnionWith(segment.Expression.FreeIdentifiers());
                }
            }
        }

        return into;
    }

    /// <summary>The top-level <c>input</c> keys this template reads through its <c>${…}</c> interpolations, via a
    /// direct <c>input.&lt;key&gt;</c>/<c>input["key"]</c> access. Backs the semantic walker's rejection of a
    /// <c>secretRef</c> input anywhere in the expression value space.</summary>
    public IReadOnlySet<string> InputMemberReferences()
    {
        var into = new HashSet<string>(StringComparer.Ordinal);
        if (_segments is not null)
        {
            foreach (var segment in _segments)
            {
                if (segment.Expression is not null)
                {
                    into.UnionWith(segment.Expression.InputMemberReferences());
                }
            }
        }

        return into;
    }

    private async ValueTask<string> RenderSegmentsAsync(IEvalScope scope, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var segment in _segments!)
        {
            if (segment.Expression is null)
            {
                sb.Append(segment.Literal);
            }
            else
            {
                sb.Append(ExpressionValues.ToStringValue(await segment.Expression.EvaluateAsync(scope, ct)));
            }
        }

        return sb.ToString();
    }

    private static int FindInterpolationEnd(string source, int start)
    {
        var depth = 1; // the '{' of '${'
        var i = start;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '\'')
            {
                i = SkipString(source, i);
                continue;
            }

            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }

            i++;
        }

        throw new ExpressionParseException(
            ExpressionErrorCodes.SyntaxError, "unterminated '${...}' interpolation", start - 2);
    }

    private static int SkipString(string source, int i)
    {
        i++; // opening quote
        while (i < source.Length && source[i] != '\'')
        {
            if (source[i] == '\\')
            {
                i++; // the escaped character is not a closing quote
            }

            i++;
        }

        return i < source.Length ? i + 1 : i;
    }

    private sealed record Segment(string? Literal, CrawldadExpression? Expression);
}
