using System.Text.Json;
using Crawldad.Web.Features.Runs.Interpreter;

namespace Crawldad.Tests.Unit;

/// <summary>JSON ↔ value-model conversion: number widening (integral ⇒ long, else double), container order, and the
/// terminal <c>handle_in_result</c> rejection when a non-serialisable handle reaches the result tree.</summary>
public class JsonValuesTests
{
    private static object? From(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonValues.FromJson(doc.RootElement);
    }

    [Fact]
    public void FromJson_maps_each_kind()
    {
        From("\"hi\"").ShouldBe("hi");
        From("42").ShouldBe(42L);            // integral ⇒ long
        From("1.5").ShouldBe(1.5d);          // fractional ⇒ double
        From("true").ShouldBe(true);
        From("false").ShouldBe(false);
        From("null").ShouldBeNull();
        JsonValues.FromJson(default).ShouldBeNull(); // Undefined (an absent optional) ⇒ null

        var list = From("[1,2]").ShouldBeAssignableTo<List<object?>>()!;
        list.ShouldBe([1L, 2L]);

        var map = From("""{ "a": 1, "b": "x" }""").ShouldBeAssignableTo<Dictionary<string, object?>>()!;
        map["a"].ShouldBe(1L);
        map["b"].ShouldBe("x");
    }

    [Fact]
    public void ToJson_round_trips_the_value_model_preserving_key_order()
    {
        var value = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["z"] = "first",
            ["a"] = 2L,
            ["frac"] = 1.5d,
            ["flag"] = true,
            ["nothing"] = null,
            ["list"] = new List<object?> { 1L, "two" },
        };

        var element = JsonValues.ToJson(value);
        element.GetRawText().ShouldBe("""{"z":"first","a":2,"frac":1.5,"flag":true,"nothing":null,"list":[1,"two"]}""");
    }

    [Fact]
    public void ToJson_rejects_a_handle_anywhere_in_the_tree()
    {
        var tree = new Dictionary<string, object?>(StringComparer.Ordinal) { ["ok"] = "x", ["bad"] = new object() };
        var error = Should.Throw<InterpreterException>(() => JsonValues.ToJson(tree));
        error.Code.ShouldBe(InterpreterErrorCodes.HandleInResult);
    }
}
