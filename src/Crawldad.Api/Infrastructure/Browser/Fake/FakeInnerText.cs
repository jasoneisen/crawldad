using System.Text;
using AngleSharp.Dom;

namespace Crawldad.Api.Infrastructure.Browser.Fake;

/// <summary>A layout-free approximation of Chromium's <c>innerText</c>: AngleSharp has no rendered text, and raw
/// <c>TextContent</c> concatenates <c>&lt;br&gt;</c>-separated lines with no separator, breaking line-split payloads.
/// Not a full CSS layout — no <c>white-space:pre</c>, no <c>display</c> overrides, no table-cell tabs.</summary>
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
