using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit.Expressions;

public class BuiltinDomTests
{
    [Fact]
    public async Task Count_of_a_string_queries_the_dom_as_a_selector()
    {
        var dom = new FakeDom { OnCount = static (_, _) => 3L };
        var scope = new FakeScope(dom);

        (await Xp.EvalAsync("count('a.sel')", scope)).ShouldBe(3L);

        var call = dom.Calls.ShouldHaveSingleItem();
        call.Op.ShouldBe("count");
        call.Target.ShouldBe("a.sel");
        call.Css.ShouldBeNull();
    }

    [Fact]
    public async Task Count_of_a_handle_queries_the_dom()
    {
        var dom = new FakeDom { OnCount = static (target, _) => target is FakeHandle ? 7L : 0L };
        var scope = new FakeScope(dom).With("row", new FakeHandle());

        (await Xp.EvalAsync("count(row)", scope)).ShouldBe(7L);
    }

    [Fact]
    public async Task Count_is_a_dom_query_but_length_is_a_string_length()
    {
        var dom = new FakeDom { OnCount = static (_, _) => 999L };
        var scope = new FakeScope(dom);

        (await Xp.EvalAsync("length('abc')", scope)).ShouldBe(3L); // string length — no DOM touch
        dom.Calls.ShouldBeEmpty();

        (await Xp.EvalAsync("count('abc')", scope)).ShouldBe(999L); // selector query
        dom.Calls.ShouldHaveSingleItem().Op.ShouldBe("count");
    }

    [Fact]
    public async Task Exists_queries_the_dom_with_and_without_relative_css()
    {
        var dom = new FakeDom { OnExists = static (_, css) => css is null or "rel" };
        var scope = new FakeScope(dom);

        (await Xp.EvalAsync("exists('sel')", scope)).ShouldBe(true);
        (await Xp.EvalAsync("exists('sel', 'rel')", scope)).ShouldBe(true);

        dom.Calls[0].Css.ShouldBeNull();
        dom.Calls[1].Op.ShouldBe("exists");
        dom.Calls[1].Css.ShouldBe("rel");
    }

    [Fact]
    public async Task Text_innerText_innerHtml_read_and_pass_relative_css_through()
    {
        var dom = new FakeDom
        {
            OnText = static (_, css) => css is null ? "T" : "T:" + css,
            OnInnerText = static (_, _) => "IT",
            OnInnerHtml = static (_, _) => "IH",
        };
        var scope = new FakeScope(dom);

        (await Xp.EvalAsync("text('sel')", scope)).ShouldBe("T");
        (await Xp.EvalAsync("text('sel', 'td:nth-child(2)')", scope)).ShouldBe("T:td:nth-child(2)");
        (await Xp.EvalAsync("innerText('sel')", scope)).ShouldBe("IT");
        (await Xp.EvalAsync("innerHtml('sel')", scope)).ShouldBe("IH");
    }

    [Fact]
    public async Task Dom_builtins_accept_an_opaque_handle_target()
    {
        var handle = new FakeHandle();
        var dom = new FakeDom { OnText = static (target, _) => target is FakeHandle ? "from-handle" : null };
        var scope = new FakeScope(dom).With("row", handle);

        (await Xp.EvalAsync("text(row)", scope)).ShouldBe("from-handle");
        dom.Calls.ShouldHaveSingleItem().Target.ShouldBeSameAs(handle);
    }

    [Fact]
    public async Task Dom_builtins_pass_a_structured_sel_map_through_untouched()
    {
        var dom = new FakeDom
        {
            OnText = static (target, _) =>
                target is Dictionary<string, object?> map ? (string?)map["css"] : "not-a-map",
        };
        var scope = new FakeScope(dom);

        (await Xp.EvalAsync("text({ css: 'the-selector', first: true })", scope)).ShouldBe("the-selector");
        dom.Calls.ShouldHaveSingleItem().Target.ShouldBeAssignableTo<Dictionary<string, object?>>();
    }

    [Fact]
    public async Task Missing_node_returns_null_and_null_propagates_through_string_builtins()
    {
        var dom = new FakeDom { OnText = static (_, _) => null };
        var scope = new FakeScope(dom);

        (await Xp.EvalAsync("text('sel')", scope)).ShouldBeNull();
        (await Xp.EvalAsync("trim(text('sel'))", scope)).ShouldBeNull();
        (await Xp.EvalAsync("coalesce(text('sel'), 'default')", scope)).ShouldBe("default");
    }

    [Fact]
    public async Task Attr_takes_two_or_three_arguments()
    {
        var dom = new FakeDom { OnAttr = static (_, css, name) => $"{css}|{name}" };
        var scope = new FakeScope(dom);

        (await Xp.EvalAsync("attr('sel', 'href')", scope)).ShouldBe("|href"); // css null
        (await Xp.EvalAsync("attr('sel', 'td:nth-child(3) a', 'href')", scope)).ShouldBe("td:nth-child(3) a|href");
    }

    [Theory]
    // invalid DOM targets (covers each arm of the RequireDomTarget rejection: null/bool/int/double/array)
    [InlineData("text(null)")]
    [InlineData("text(true)")]
    [InlineData("text(5)")]
    [InlineData("text(1.5)")]
    [InlineData("text([1])")]
    [InlineData("exists(5)")]
    [InlineData("exists(null)")]
    // relative css / attribute name must be strings
    [InlineData("text('sel', 5)")]
    [InlineData("attr(5, 'href')")]
    [InlineData("attr('sel', 5)")]
    [InlineData("attr('sel', 5, 'href')")]
    [InlineData("attr('sel', 'css', 5)")]
    public async Task Dom_target_and_argument_type_errors(string source) =>
        (await Xp.EvalErrorAsync(source)).Code.ShouldBe(ExpressionErrorCodes.TypeError);
}
