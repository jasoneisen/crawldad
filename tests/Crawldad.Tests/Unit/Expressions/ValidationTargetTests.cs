using Crawldad.Api.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

/// <summary>The exact expressions from the real search payload that must parse and evaluate correctly.</summary>
public class ValidationTargetTests
{
    private static Dictionary<string, object?> Map(params (string Key, object? Value)[] entries)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            map[key] = value;
        }

        return map;
    }

    [Theory]
    [InlineData("08/06/2026", true)]   // present, non-blank ⇒ true
    [InlineData("   ", false)]          // present, blank ⇒ false
    public async Task Date_fill_guard_present(string startDate, bool expected)
    {
        var scope = new FakeScope().With("input", Map(("startDate", startDate)));
        (await Xp.EvalAsync("!isNullOrWhitespace(input.startDate)", scope)).ShouldBe(expected);
    }

    [Fact]
    public async Task Date_fill_guard_absent_key_is_false()
    {
        var scope = new FakeScope().With("input", Map()); // no startDate key ⇒ member access ⇒ null
        (await Xp.EvalAsync("!isNullOrWhitespace(input.startDate)", scope)).ShouldBe(false);
        (await Xp.EvalAsync("input.startDate", scope)).ShouldBeNull();
    }

    [Fact]
    public async Task Row_range_bound_over_an_array()
    {
        var scope = new FakeScope().With("rows", new List<object?> { 1L, 2L, 3L, 4L, 5L });
        (await Xp.EvalAsync("count(rows) - 2", scope)).ShouldBe(3L);
    }

    [Fact]
    public async Task Row_range_bound_over_a_handle()
    {
        var dom = new FakeDom { OnCount = static (target, _) => target is FakeHandle ? 5L : 0L };
        var scope = new FakeScope(dom).With("rows", new FakeHandle());
        (await Xp.EvalAsync("count(rows) - 2", scope)).ShouldBe(3L);
    }

    [Theory]
    [InlineData(1L, true)]
    [InlineData(0L, false)]
    public async Task Pagination_has_next_link(long matched, bool expected)
    {
        var dom = new FakeDom { OnCount = (_, _) => matched };
        var scope = new FakeScope(dom);
        (await Xp.EvalAsync("count('table.aca_pagination td:last-child a') > 0", scope)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(true, true, false, true)]    // hitKnown && crawledToEnd
    [InlineData(true, false, false, false)]  // hitKnown && !crawledToEnd
    [InlineData(false, false, true, false)]  // !hitKnown ⇒ !hasMorePages(true) ⇒ false
    [InlineData(false, false, false, true)]  // !hitKnown ⇒ !hasMorePages(false) ⇒ true
    public async Task Should_continue_negation(bool hitKnown, bool crawledToEnd, bool hasMorePages, bool expected)
    {
        var scope = new FakeScope()
            .With("hitKnown", hitKnown)
            .With("crawledToEnd", crawledToEnd)
            .With("hasMorePages", hasMorePages);
        (await Xp.EvalAsync("hitKnown ? crawledToEnd : !hasMorePages", scope)).ShouldBe(expected);
    }

    [Fact]
    public async Task Empty_collection_literals()
    {
        (await Xp.EvalAsync("[]")).ShouldBeAssignableTo<List<object?>>()!.ShouldBeEmpty();
        (await Xp.EvalAsync("{}")).ShouldBeAssignableTo<Dictionary<string, object?>>()!.ShouldBeEmpty();
    }

    [Fact]
    public async Task Per_row_object_literal_from_a_handle_and_faked_dom()
    {
        var row = new FakeHandle();
        var dom = new FakeDom
        {
            // padded/nullable cell text, keyed by relative css — trim + coalesce clean it up
            OnText = static (_, css) => css switch
            {
                "td:nth-child(2)" => "  08/06/2026  ",
                "td:nth-child(3) a" => "  ENF-001  ",
                "td:nth-child(4)" => "  Enforcement  ",
                "td:nth-child(5)" => "  123 Main St  ",
                "td:nth-child(6)" => "  Open  ",
                "td:nth-child(7)" => null, // missing cell ⇒ coalesce(null,'') ⇒ ''
                _ => null,
            },
            OnAttr = static (_, css, name) =>
                string.Equals(css, "td:nth-child(3) a", StringComparison.Ordinal)
                && string.Equals(name, "href", StringComparison.Ordinal)
                    ? "  /Cap/CapDetail.aspx?id=1  "
                    : null,
        };
        var scope = new FakeScope(dom, "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx").With("row", row);

        const string Source =
            "{ url: '' + urlScheme(pageUrl()) + '://' + urlHost(pageUrl()) + trim(coalesce(attr(row,'td:nth-child(3) a','href'),'')), " +
            "data: { date: trim(coalesce(text(row,'td:nth-child(2)'),'')), " +
            "recordNumber: trim(coalesce(text(row,'td:nth-child(3) a'),'')), " +
            "recordType: trim(coalesce(text(row,'td:nth-child(4)'),'')), " +
            "address: trim(coalesce(text(row,'td:nth-child(5)'),'')), " +
            "status: trim(coalesce(text(row,'td:nth-child(6)'),'')), " +
            "shortNotes: trim(coalesce(text(row,'td:nth-child(7)'),'')) } }";

        var result = (await Xp.EvalAsync(Source, scope)).ShouldBeAssignableTo<Dictionary<string, object?>>()!;
        result["url"].ShouldBe("https://aca-prod.accela.com/Cap/CapDetail.aspx?id=1");

        var data = result["data"].ShouldBeAssignableTo<Dictionary<string, object?>>()!;
        data["date"].ShouldBe("08/06/2026");
        data["recordNumber"].ShouldBe("ENF-001");
        data["recordType"].ShouldBe("Enforcement");
        data["address"].ShouldBe("123 Main St");
        data["status"].ShouldBe("Open");
        data["shortNotes"].ShouldBe("");
    }

    [Fact]
    public void Negative_targets_are_rejected_at_parse_time()
    {
        Xp.ParseError("foo(1)").Code.ShouldBe(ExpressionErrorCodes.UnknownFunction);
        Xp.ParseError("trim()").Code.ShouldBe(ExpressionErrorCodes.WrongArity);
        Xp.ParseError("1 +").Code.ShouldBe(ExpressionErrorCodes.SyntaxError);
    }

    [Fact]
    public async Task Negative_targets_are_terminal_at_eval_time()
    {
        (await Xp.EvalErrorAsync("'a' < 'b'")).Code.ShouldBe(ExpressionErrorCodes.TypeError);
        (await Xp.EvalErrorAsync("[1,2][5]")).Code.ShouldBe(ExpressionErrorCodes.IndexOutOfRange);
        (await Xp.EvalErrorAsync("null && true")).Code.ShouldBe(ExpressionErrorCodes.TypeError);
    }
}
