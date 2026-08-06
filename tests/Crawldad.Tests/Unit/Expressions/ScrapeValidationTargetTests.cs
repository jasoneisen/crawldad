using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

/// <summary>
/// The exact expressions from the scrape payload (Appendix B.2) that this work package must serve — each parsed and
/// evaluated against a faked scope/DOM. These are the fidelity targets: the related-records indent query, the
/// address/processing split chains, the attachment filename composition, the trailing-colon strip, and the
/// related-record link resolution.
/// </summary>
public class ScrapeValidationTargetTests
{
    // ----- related records: greatest indent strictly less than current (:625-697, §7.4) -------------------------

    [Fact]
    public async Task Parent_resolution_selects_the_greatest_lesser_indent()
    {
        var scope = new FakeScope()
            .With("parents", Val.Map(("0", "ROOT"), ("12", "MID"), ("24", "LEAF")))
            .With("indent", 15L);

        // cand = filter(map(keys(parents), k, toInt(k)), n, n < indent)
        var cand = await Xp.EvalAsync("filter(map(keys(parents), k, toInt(k)), n, n < indent)", scope);
        scope.With("cand", cand);

        (await Xp.EvalAsync("count(cand) > 0 ? get(parents, string(max(cand))) : ''", scope)).ShouldBe("MID");
    }

    [Fact]
    public async Task Parent_resolution_is_empty_when_no_lesser_indent_exists()
    {
        var scope = new FakeScope()
            .With("parents", Val.Map(("12", "MID"), ("24", "LEAF")))
            .With("indent", 0L);

        var cand = await Xp.EvalAsync("filter(map(keys(parents), k, toInt(k)), n, n < indent)", scope);
        scope.With("cand", cand);

        (await Xp.EvalAsync("count(cand) > 0 ? get(parents, string(max(cand))) : ''", scope)).ShouldBe("");
    }

    // ----- location address: split innerHTML on <br>, then on <span> (:229-268) --------------------------------

    [Fact]
    public async Task Address_line_count_and_city_state_zip_extraction()
    {
        const string Html = "123 Main St<br>Springfield, IL 62701<span>map</span><br>x";
        var scope = new FakeScope().With("html", Html);

        (await Xp.EvalAsync("length(split(html, '<br>'))", scope)).ShouldBe(3L);
        (await Xp.EvalAsync("split(split(trim(html),'<br>')[1], '<span')[0]", scope)).ShouldBe("Springfield, IL 62701");
    }

    // ----- processing status: chained split/replace (:489-493) -------------------------------------------------

    [Fact]
    public async Task Processing_status_due_and_marked_on_chains()
    {
        var scope = new FakeScope().With("lines", Val.List(
            "Due on 5/1/2024, assigned to John",
            "Marked as Complete on 6/1/2024 by Jane"));

        (await Xp.EvalAsync("replace(trim(split(lines[0],',')[0]), 'Due on ', '')", scope)).ShouldBe("5/1/2024");
        (await Xp.EvalAsync("trim(split(split(lines[1],' on ')[1], ' by ')[0])", scope)).ShouldBe("6/1/2024");
        (await Xp.EvalAsync("trim(split(split(lines[1],' on ')[1], ' by ')[1])", scope)).ShouldBe("Jane");
    }

    // ----- known-URL early-termination scan (:81-105) ----------------------------------------------------------

    [Theory]
    [InlineData("u2", true)]
    [InlineData("u9", false)]
    public async Task Any_known_url_matches_the_current_row(string rowValue, bool expected)
    {
        var scope = new FakeScope()
            .With("input", Val.Map(("knownUrls", Val.List("u1", "u2"))))
            .With("pageResults", Val.List(Val.Map(("url", rowValue))))
            .With("j", 0L);

        (await Xp.EvalAsync("any(input.knownUrls, u, u == pageResults[j].url)", scope)).ShouldBe(expected);
    }

    // ----- attachment internalFilename composition (:576) ------------------------------------------------------

    [Theory]
    [InlineData("report.final.pdf", "cid-abc.pdf")]
    [InlineData("noextension", "cid-abc")]
    public async Task Internal_filename_appends_extension_only_when_present(string filename, string expected)
    {
        var scope = new FakeScope()
            .With("dl", Val.Map(("contentId", "cid-abc")))
            .With("filename", filename);

        const string Source =
            "string(dl.contentId) + (contains(filename,'.') ? '.' + substringAfterLast(filename,'.') : '')";
        (await Xp.EvalAsync(Source, scope)).ShouldBe(expected);
    }

    // ----- additional-comment heading: trailing-colon strip = h[..^1] (:507) -----------------------------------

    [Theory]
    [InlineData("Category:", "Category")]
    [InlineData("Category", "Category")]
    public async Task Trailing_colon_strip(string heading, string expected)
    {
        var scope = new FakeScope().With("h", heading);
        (await Xp.EvalAsync("endsWith(h,':') ? substring(h,0,length(h)-1) : h", scope)).ShouldBe(expected);
    }

    // ----- newLinks de-duplication (HistoricalCrawler:79) ------------------------------------------------------

    [Fact]
    public async Task Distinct_newLinks_preserves_first_occurrence_order()
    {
        var scope = new FakeScope().With("newLinks", Val.List("a", "b", "a", "c", "b"));
        var result = (await Xp.EvalAsync("distinct(newLinks)", scope)).ShouldBeAssignableTo<List<object?>>()!;
        result.ShouldBe(["a", "b", "c"]);
    }

    // ----- related-record link resolution (:672) — fake attr, pinned C# Uri result ------------------------------

    [Fact]
    public async Task Related_record_link_resolves_against_the_record_base()
    {
        var rb = new FakeHandle();
        var dom = new FakeDom
        {
            OnAttr = static (target, css, name) =>
                target is FakeHandle
                && string.Equals(css, "> td:last-child a", StringComparison.Ordinal)
                && string.Equals(name, "href", StringComparison.Ordinal)
                    ? "  CapDetail.aspx?id=99  "
                    : null,
        };
        var scope = new FakeScope(dom)
            .With("input", Val.Map(("link", "https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx")))
            .With("rb", rb);

        (await Xp.EvalAsync("resolveUrl(input.link, trim(attr(rb,'> td:last-child a','href')))", scope))
            .ShouldBe("https://aca-prod.accela.com/LJCMG/Cap/CapDetail.aspx?id=99");
    }

    // ----- terminal: split-then-index out of range reproduces the C# Split(...)[i] throw (:438/:489) -----------

    [Fact]
    public async Task Split_then_out_of_range_index_is_terminal()
    {
        (await Xp.EvalErrorAsync("split('a,b', ',')[5]")).Code.ShouldBe(ExpressionErrorCodes.IndexOutOfRange);
    }
}
