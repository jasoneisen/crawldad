using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

/// <summary>
/// <c>resolveUrl(base, rel)</c> = <c>new Uri(new Uri(base), rel).ToString()</c> — the reference's proper RFC
/// resolution for related-record links (:672, §7.3), distinct from the search rows' naive concat. The expected values
/// below are <b>pinned to the C# <see cref="System.Uri"/> output</b> (verified against the runtime), so this doubles
/// as the golden contract a later cross-check compares against.
/// </summary>
public class ResolveUrlTests
{
    [Theory]
    // absolute rel replaces the base entirely
    [InlineData("https://h/a/b", "https://other/x", "https://other/x")]
    // query-only rel keeps the base path, swaps the query
    [InlineData("https://h/a/b?old=1", "?new=2", "https://h/a/b?new=2")]
    // relative path resolves against the base's directory
    [InlineData("https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx", "CapDetail.aspx?id=5",
        "https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?id=5")]
    // dot / dot-dot / root / fragment forms
    [InlineData("https://h/a/b/c", "./d", "https://h/a/b/d")]
    [InlineData("https://h/a/b/c", "../d", "https://h/a/d")]
    [InlineData("https://h/a/b/c", "/x/y", "https://h/x/y")]
    [InlineData("https://h/a/b", "#frag", "https://h/a/b#frag")]
    // empty rel returns the base (query preserved)
    [InlineData("https://h/a/b?q=1", "", "https://h/a/b?q=1")]
    public async Task Resolves_to_the_pinned_csharp_uri(string baseText, string rel, string expected)
    {
        var scope = new FakeScope().With("b", baseText).With("r", rel);
        (await Xp.EvalAsync("resolveUrl(b, r)", scope)).ShouldBe(expected);
    }

    [Theory]
    // base must be a valid absolute URL — null / non-absolute / non-string are all invalid_url (like urlScheme et al.)
    [InlineData("resolveUrl(null, 'x')", ExpressionErrorCodes.InvalidUrl)]
    [InlineData("resolveUrl('not a url', 'x')", ExpressionErrorCodes.InvalidUrl)]
    [InlineData("resolveUrl(5, 'x')", ExpressionErrorCodes.InvalidUrl)]
    // rel must be a string
    [InlineData("resolveUrl('https://h/', null)", ExpressionErrorCodes.TypeError)]
    [InlineData("resolveUrl('https://h/', 5)", ExpressionErrorCodes.TypeError)]
    // a malformed rel the Uri resolver rejects is invalid_url (the catch branch)
    [InlineData("resolveUrl('https://h/a/b', 'http://[bad')", ExpressionErrorCodes.InvalidUrl)]
    [InlineData("resolveUrl('https://h/a/b', 'c:d')", ExpressionErrorCodes.InvalidUrl)]
    public async Task Resolve_url_terminal_failures(string source, string expectedCode) =>
        (await Xp.EvalErrorAsync(source)).Code.ShouldBe(expectedCode);
}
