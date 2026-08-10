using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>Parses a <c>set</c> node's <c>path</c> into ordered segments: dotted literal names and <c>[${Expr}]</c>
/// computed segments, composable (<c>a.b[${k}]</c>). A bracket run is scanned for its closing <c>]</c> while skipping
/// single-quoted strings, so a key expression may itself contain a <c>']'</c> literal.</summary>
internal static class SetPath
{
    /// <summary>Parses <paramref name="path"/> into its segments. An unmatched <c>[</c> is a terminal
    /// <c>malformed_node</c>.</summary>
    public static IReadOnlyList<PathSegment> Parse(string path)
    {
        var segments = new List<PathSegment>();
        var i = 0;
        while (i < path.Length)
        {
            if (path[i] == '[')
            {
                var end = FindClosingBracket(path, i + 1);
                segments.Add(new ComputedSegment(CrawldadTemplate.Parse(path[(i + 1)..end])));
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < path.Length && path[i] != '.' && path[i] != '[')
                {
                    i++;
                }

                segments.Add(new LiteralSegment(path[start..i]));
            }

            // A '.' only separates segments; consume it so the next iteration starts on a name or '['.
            if (i < path.Length && path[i] == '.')
            {
                i++;
            }
        }

        return segments;
    }

    private static int FindClosingBracket(string path, int start)
    {
        var i = start;
        while (i < path.Length)
        {
            if (path[i] == '\'')
            {
                i = SkipString(path, i);
                continue;
            }

            if (path[i] == ']')
            {
                return i;
            }

            i++;
        }

        throw new InterpreterException(InterpreterErrorCodes.MalformedNode, $"unterminated '[' in set path '{path}'");
    }

    // Advances past a single-quoted string so a ']' inside it is not mistaken for the segment's closing bracket.
    private static int SkipString(string path, int i)
    {
        i++; // the opening quote
        while (i < path.Length && path[i] != '\'')
        {
            i++;
        }

        return Math.Min(i + 1, path.Length); // past the closing quote, clamped for an unterminated string
    }
}

/// <summary>One resolved step of a <c>set</c> path: a fixed key or a template-rendered key.</summary>
internal abstract record PathSegment
{
    /// <summary>Resolves this segment to its map key against <paramref name="scope"/>.</summary>
    public abstract ValueTask<string> KeyAsync(IEvalScope scope, CancellationToken ct);
}

/// <summary>A fixed dotted key (e.g. <c>title</c> in <c>path:"title"</c>).</summary>
internal sealed record LiteralSegment(string Name) : PathSegment
{
    public override ValueTask<string> KeyAsync(IEvalScope scope, CancellationToken ct) => new(Name);
}

/// <summary>A computed key (e.g. <c>[${indent}]</c>): the template renders to the key at mutation time.</summary>
internal sealed record ComputedSegment(CrawldadTemplate Template) : PathSegment
{
    public override ValueTask<string> KeyAsync(IEvalScope scope, CancellationToken ct) => Template.RenderAsync(scope, ct);
}
