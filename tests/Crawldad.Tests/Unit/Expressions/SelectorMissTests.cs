using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

/// <summary>The extraction-builtin selector-miss semantics at the expression layer: <c>text</c>/<c>innerText</c>/
/// <c>innerHtml</c>/<c>attr</c> report a miss when their target matched NO element (distinct from a matched-but-empty
/// element), the predicates <c>count</c>/<c>exists</c> never do, and <c>require(...)</c> promotes a miss to a terminal
/// <c>selector_miss</c>. The counter/event/dedupe live in the interpreter (<see cref="Unit.StrictExtractionTests"/>);
/// here the recording sink stands in for it.</summary>
public class SelectorMissTests
{
    private static (FakeScope Scope, RecordingMissSink Sink) ScopeWith(FakeDom dom)
    {
        var sink = new RecordingMissSink();
        return (new FakeScope(dom, misses: sink), sink);
    }

    [Fact]
    public async Task Text_matching_no_element_records_a_soft_miss_and_null_propagates()
    {
        var dom = new FakeDom { OnText = static (_, _) => null };
        var (scope, sink) = ScopeWith(dom);

        // The documented drift idiom: coalesce hides the null, but the miss is still recorded (that IS the drift signal).
        (await Xp.EvalAsync("trim(coalesce(text('td:nth-child(3) a'), ''))", scope)).ShouldBe("");

        var miss = sink.Records.ShouldHaveSingleItem();
        miss.Selector.ShouldBe("td:nth-child(3) a");
        miss.Required.ShouldBeFalse();
    }

    [Fact]
    public async Task A_matched_but_empty_element_is_not_a_miss()
    {
        // A zero-match reads null; a matched-but-empty element reads "" — legitimately blank data, never a miss.
        var dom = new FakeDom { OnText = static (_, _) => string.Empty };
        var (scope, sink) = ScopeWith(dom);

        (await Xp.EvalAsync("text('span.blank')", scope)).ShouldBe("");
        sink.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task InnerText_and_innerHtml_matching_no_element_record_a_miss()
    {
        var dom = new FakeDom { OnInnerText = static (_, _) => null, OnInnerHtml = static (_, _) => null };
        var (scope, sink) = ScopeWith(dom);

        (await Xp.EvalAsync("innerText('a')", scope)).ShouldBeNull();
        (await Xp.EvalAsync("innerHtml('b')", scope)).ShouldBeNull();

        sink.Records.Select(r => r.Selector).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task Attr_with_no_matching_element_is_a_miss()
    {
        // attr null + a zero count ⇒ nothing matched ⇒ a miss.
        var dom = new FakeDom { OnAttr = static (_, _, _) => null, OnCount = static (_, _) => 0L };
        var (scope, sink) = ScopeWith(dom);

        (await Xp.EvalAsync("attr('#x', 'href')", scope)).ShouldBeNull();
        sink.Records.ShouldHaveSingleItem().Selector.ShouldBe("#x");
    }

    [Fact]
    public async Task Attr_on_a_matched_element_lacking_the_attribute_is_not_a_miss()
    {
        // attr null but a non-zero count ⇒ the element matched, it just lacks the attribute ⇒ legitimately blank, no miss.
        var dom = new FakeDom { OnAttr = static (_, _, _) => null, OnCount = static (_, _) => 1L };
        var (scope, sink) = ScopeWith(dom);

        (await Xp.EvalAsync("attr('#x', 'data-absent')", scope)).ShouldBeNull();
        sink.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task Count_and_exists_are_predicates_and_never_record_a_miss()
    {
        var dom = new FakeDom { OnCount = static (_, _) => 0L, OnExists = static (_, _) => false };
        var (scope, sink) = ScopeWith(dom);

        (await Xp.EvalAsync("count('#none')", scope)).ShouldBe(0L);
        (await Xp.EvalAsync("exists('#none')", scope)).ShouldBe(false);
        sink.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task Require_makes_a_missing_extraction_a_terminal_selector_miss()
    {
        var dom = new FakeDom { OnText = static (_, _) => null };
        var (scope, sink) = ScopeWith(dom);

        var error = await Xp.EvalErrorAsync("require(text('#recordNumber'))", scope);
        error.Code.ShouldBe(ExpressionErrorCodes.SelectorMiss);
        error.Message.ShouldContain("#recordNumber");
        sink.Records.ShouldHaveSingleItem().Required.ShouldBeTrue(); // require flowed required:true to the sink
    }

    [Theory]
    [InlineData("trim(require(text('#a')))")]   // require wrapped BY another builtin
    [InlineData("require(trim(text('#a')))")]   // require wrapping another builtin
    [InlineData("require(coalesce(text('#a'), text('#b')))")] // promotes misses across a coalesce subtree
    [InlineData("require(map([1], v, text('#a')))")] // and through a binding-builtin body
    public async Task Require_promotes_misses_throughout_its_argument_subtree(string source)
    {
        var dom = new FakeDom { OnText = static (_, _) => null };
        (await Xp.EvalErrorAsync(source, new FakeScope(dom))).Code.ShouldBe(ExpressionErrorCodes.SelectorMiss);
    }

    [Fact]
    public async Task Require_on_a_matching_extraction_returns_the_value_unchanged()
    {
        var dom = new FakeDom { OnText = static (_, _) => "R-123" };
        (await Xp.EvalAsync("require(text('#recordNumber'))", new FakeScope(dom))).ShouldBe("R-123");
    }

    [Fact]
    public async Task Require_without_an_extraction_inside_is_a_transparent_passthrough()
    {
        (await Xp.EvalAsync("require('literal')")).ShouldBe("literal");
        (await Xp.EvalAsync("require(1 + 2)")).ShouldBe(3L);
    }

    [Fact]
    public async Task A_strict_sink_makes_even_an_unrequired_miss_terminal()
    {
        var dom = new FakeDom { OnText = static (_, _) => null };
        var scope = new FakeScope(dom, misses: new RecordingMissSink { Strict = true });

        (await Xp.EvalErrorAsync("text('#x')", scope)).Code.ShouldBe(ExpressionErrorCodes.SelectorMiss);
    }

    [Fact]
    public async Task The_miss_description_composes_an_opaque_handle_base_with_the_relative_css()
    {
        var dom = new FakeDom { OnText = static (_, _) => null };
        var (scope, sink) = ScopeWith(dom);
        scope.With("row", new FakeHandle());

        await Xp.EvalAsync("text(row, 'td:nth-child(3) a')", scope);
        sink.Records.ShouldHaveSingleItem().Selector.ShouldBe("<handle> td:nth-child(3) a");
    }

    [Theory]
    [InlineData("text({ css: 'div#rec', first: true })", "div#rec")] // the bare css string (the common case)
    [InlineData("text({ xpath: '//div' })", "xpath=//div")] // a non-css root → "<kind>=<value>"
    [InlineData("text({ nth: 1 })", "<sel>")] // a locator-less map (no css/xpath/text/role/title/base) → placeholder
    public async Task A_structured_sel_map_miss_is_described_by_its_primary_locator(string source, string expected)
    {
        var dom = new FakeDom { OnText = static (_, _) => null };
        var (scope, sink) = ScopeWith(dom);

        await Xp.EvalAsync(source, scope);
        sink.Records.ShouldHaveSingleItem().Selector.ShouldBe(expected);
    }
}
