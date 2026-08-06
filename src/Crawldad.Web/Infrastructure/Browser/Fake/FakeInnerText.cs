using System.Text;
using AngleSharp.Dom;

namespace Crawldad.Web.Infrastructure.Browser.Fake;

/// <summary>
/// A layout-free approximation of Chromium's <c>innerText</c> for the record/replay fake (the "innerText trap",
/// § Phase 2 WP3). AngleSharp does no layout and has no rendered text, so the fake previously returned raw
/// <c>TextContent</c> — which concatenates <c>&lt;br&gt;</c>-separated lines with <b>no separator</b>, unlike a real
/// browser. That is a fidelity bug for the processing-status region, whose payload does
/// <c>split(innerText(lineBlock), '\n')</c>: under raw TextContent the two lines fuse into one and the
/// <c>lines[1]</c> access throws. This renderer reproduces what Chromium's innerText yields for the captured markup:
/// <list type="bullet">
///   <item><c>&lt;br&gt;</c> becomes a newline.</item>
///   <item>A block-level element boundary becomes a newline (collapsed — leading/trailing/duplicate blank lines drop).</item>
///   <item>Inline ASCII whitespace runs collapse to a single space and each line is trimmed.</item>
/// </list>
/// It is deliberately <b>not</b> a full CSS layout: no <c>white-space:pre</c>, no <c>display</c> overrides, no
/// table-cell tab separators, no non-ASCII-whitespace nuance. That is sufficient for the synthesized fixtures and is
/// re-gated against real Chromium in Phase 4 (fake ≡ real). See the per-fixture FIXTURE_NOTES for the reasoning.
/// </summary>
internal static class FakeInnerText
{
    // Block-level element names whose boundaries introduce a line break (the subset the captured DOM uses; a real
    // browser derives this from computed `display`, which AngleSharp cannot).
    private static readonly HashSet<string> _blockElements = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "div", "dd", "dl", "dt", "fieldset", "figcaption", "figure",
        "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p",
        "pre", "section", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "ul",
    };

    /// <summary>Renders <paramref name="element"/>'s approximate rendered innerText.</summary>
    /// <param name="element">The element whose descendants' rendered text to produce.</param>
    /// <returns>The newline-joined, whitespace-collapsed lines.</returns>
    public static string Render(IElement element)
    {
        var sb = new StringBuilder();
        Walk(element, sb);
        return Collapse(sb.ToString());
    }

    private static void Walk(INode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText text)
            {
                sb.Append(text.Data);
            }
            else if (child is IElement element)
            {
                if (string.Equals(element.LocalName, "br", StringComparison.Ordinal))
                {
                    sb.Append('\n'); // a <br> always contributes a newline (preserving intentional blank lines)
                }
                else if (_blockElements.Contains(element.LocalName))
                {
                    EnsureBoundary(sb);
                    Walk(element, sb);
                    EnsureBoundary(sb);
                }
                else
                {
                    Walk(element, sb); // inline element — no line boundary
                }
            }
        }
    }

    // A block boundary contributes a SINGLE newline: append one only when there is preceding content not already
    // ending at a newline. So adjacent and nested block boundaries collapse to one line break (matching a browser),
    // while a <br> — appended directly above — still stacks to preserve deliberate blank lines.
    private static void EnsureBoundary(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] != '\n')
        {
            sb.Append('\n');
        }
    }

    // Splits on the hard '\n' breaks emitted above, collapses inline whitespace within each line and trims it, then
    // drops the empty lines that wrapping block boundaries produced at the ends — matching how a browser renders the
    // captured cells.
    private static string Collapse(string raw)
    {
        var lines = raw.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = CollapseInline(lines[i]).Trim();
        }

        var start = 0;
        var end = lines.Length;
        while (start < end && lines[start].Length == 0)
        {
            start++;
        }

        while (end > start && lines[end - 1].Length == 0)
        {
            end--;
        }

        return string.Join('\n', lines[start..end]);
    }

    private static string CollapseInline(string line)
    {
        var sb = new StringBuilder(line.Length);
        var pendingSpace = false;
        foreach (var ch in line)
        {
            if (ch is ' ' or '\t' or '\r' or '\f')
            {
                pendingSpace = true;
            }
            else
            {
                if (pendingSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(ch);
                pendingSpace = false;
            }
        }

        return sb.ToString();
    }
}
