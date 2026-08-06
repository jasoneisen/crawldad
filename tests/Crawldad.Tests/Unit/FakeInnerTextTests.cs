using AngleSharp.Html.Parser;
using Crawldad.Web.Infrastructure.Browser.Fake;

namespace Crawldad.Tests.Unit;

/// <summary>
/// Unit coverage for <see cref="FakeInnerText"/>, the fake backend's layout-free approximation of Chromium's
/// <c>innerText</c> (the "innerText trap"): <c>&lt;br&gt;</c> and block boundaries become newlines, inline whitespace
/// collapses, and boundary blank lines drop. The processing-status integration test exercises the load-bearing
/// <c>&lt;br&gt;</c> path end-to-end; these pin the remaining shapes.
/// </summary>
public class FakeInnerTextTests
{
    private static readonly HtmlParser _parser = new();

    private static string Render(string innerHtml)
    {
        var doc = _parser.ParseDocument("<div id='root'></div>");
        var root = doc.QuerySelector("#root")!;
        root.InnerHtml = innerHtml;
        return FakeInnerText.Render(root);
    }

    [Theory]
    // plain text — a single clean cell (the shape the existing DOM tests rely on)
    [InlineData("Enforcement", "Enforcement")]
    // <br> becomes a newline — the processing-status line separator
    [InlineData("Due on X, assigned to Y<br>Marked as S on D by B", "Due on X, assigned to Y\nMarked as S on D by B")]
    // a leading block element introduces a boundary newline, then the following text — the leading blank line drops
    [InlineData("<div>x</div>y", "x\ny")]
    // a trailing block element — the trailing blank line drops
    [InlineData("x<div>y</div>", "x\ny")]
    // inline elements introduce NO boundary (span is not block) — the two runs fuse
    [InlineData("<span>a</span><span>b</span>", "ab")]
    // inline whitespace runs collapse to a single space; leading/trailing trim
    [InlineData("  A  B ", "A B")]
    // a comment node is neither text nor element — skipped
    [InlineData("<!--c-->text", "text")]
    // sibling block elements collapse to ONE newline between them (a browser does not double the boundary)
    [InlineData("<div>a</div><div>b</div>", "a\nb")]
    // nested block boundaries also collapse to one newline (the boundary is a separator, not a repeat)
    [InlineData("<div><div>a</div></div><div>b</div>", "a\nb")]
    // a leading <br> produces a leading blank line that drops
    [InlineData("<br>after", "after")]
    // consecutive <br> are PRESERVED as interior blank lines (deliberate, unlike collapsed block boundaries)
    [InlineData("A<br><br>B", "A\n\nB")]
    // an element with only a <br> is all-blank — leading and trailing trims consume every line, leaving ""
    [InlineData("<br>", "")]
    public void Renders_chromium_like_inner_text(string innerHtml, string expected) =>
        Render(innerHtml).ShouldBe(expected);
}
