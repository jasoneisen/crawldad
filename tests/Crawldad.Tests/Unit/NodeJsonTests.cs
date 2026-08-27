using System.Text.Json;
using Crawldad.Api.Features.Runs.Interpreter;

namespace Crawldad.Tests.Unit;

/// <summary>The classified typed reads of run-payload JSON (issue #48): each helper returns the value on the happy path
/// and raises a terminal <c>malformed_node</c> <see cref="InterpreterException"/> for a missing or wrong-kinded field —
/// the guard that keeps an UNVALIDATED inline payload's wrong-typed field from escaping as a raw-accessor 500.</summary>
public class NodeJsonTests
{
    private static JsonElement El(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static void ShouldBeMalformed(Action read) =>
        Should.Throw<InterpreterException>(read).Code.ShouldBe(InterpreterErrorCodes.MalformedNode);

    [Fact]
    public void RequireString_takes_a_string_and_rejects_missing_or_wrong_kind()
    {
        NodeJson.RequireString(El("""{ "x": "hi" }"""), "x").ShouldBe("hi");
        ShouldBeMalformed(() => NodeJson.RequireString(El("{}"), "x"));            // missing
        ShouldBeMalformed(() => NodeJson.RequireString(El("""{ "x": 5 }"""), "x")); // wrong kind
    }

    [Fact]
    public void OptionalString_is_null_when_absent_and_rejects_a_wrong_kind()
    {
        NodeJson.OptionalString(El("{}"), "x").ShouldBeNull();
        NodeJson.OptionalString(El("""{ "x": "hi" }"""), "x").ShouldBe("hi");
        ShouldBeMalformed(() => NodeJson.OptionalString(El("""{ "x": 5 }"""), "x"));
    }

    [Fact]
    public void OptionalBool_defaults_when_absent_and_rejects_a_non_bool()
    {
        NodeJson.OptionalBool(El("{}"), "x", true).ShouldBeTrue();
        NodeJson.OptionalBool(El("{}"), "x", false).ShouldBeFalse();
        NodeJson.OptionalBool(El("""{ "x": true }"""), "x", false).ShouldBeTrue();
        NodeJson.OptionalBool(El("""{ "x": false }"""), "x", true).ShouldBeFalse();
        ShouldBeMalformed(() => NodeJson.OptionalBool(El("""{ "x": "yes" }"""), "x", false));
    }

    [Fact]
    public void OptionalInt_defaults_when_absent_and_rejects_a_non_integer()
    {
        NodeJson.OptionalInt(El("{}"), "x", 7).ShouldBe(7);
        NodeJson.OptionalInt(El("""{ "x": 3 }"""), "x", 7).ShouldBe(3);
        ShouldBeMalformed(() => NodeJson.OptionalInt(El("""{ "x": "3" }"""), "x", 7));    // not a number
        ShouldBeMalformed(() => NodeJson.OptionalInt(El("""{ "x": 9999999999 }"""), "x", 7)); // out of Int32 range
    }

    [Fact]
    public void RequireObject_takes_an_object_and_rejects_missing_or_wrong_kind()
    {
        NodeJson.RequireObject(El("""{ "x": { "a": 1 } }"""), "x").ValueKind.ShouldBe(JsonValueKind.Object);
        ShouldBeMalformed(() => NodeJson.RequireObject(El("{}"), "x"));
        ShouldBeMalformed(() => NodeJson.RequireObject(El("""{ "x": 5 }"""), "x"));
    }

    [Fact]
    public void OptionalObject_is_null_when_absent_and_rejects_a_wrong_kind()
    {
        NodeJson.OptionalObject(El("{}"), "x").ShouldBeNull();
        NodeJson.OptionalObject(El("""{ "x": {} }"""), "x").ShouldNotBeNull();
        ShouldBeMalformed(() => NodeJson.OptionalObject(El("""{ "x": 5 }"""), "x"));
    }

    [Fact]
    public void OptionalStringArray_is_empty_when_absent_and_rejects_a_non_array_or_non_string_element()
    {
        NodeJson.OptionalStringArray(El("{}"), "x").ShouldBeEmpty();
        NodeJson.OptionalStringArray(El("""{ "x": [ "a", "b" ] }"""), "x").ShouldBe(["a", "b"]);
        ShouldBeMalformed(() => NodeJson.OptionalStringArray(El("""{ "x": 5 }"""), "x"));       // not an array
        ShouldBeMalformed(() => NodeJson.OptionalStringArray(El("""{ "x": [ 5 ] }"""), "x"));   // non-string element
    }

    [Fact]
    public void RequireElement_takes_a_present_value_and_rejects_a_missing_one()
    {
        NodeJson.RequireElement(El("""{ "x": [ 1 ] }"""), "x").ValueKind.ShouldBe(JsonValueKind.Array);
        ShouldBeMalformed(() => NodeJson.RequireElement(El("{}"), "x"));
    }

    [Fact]
    public void RequireStringValue_rejects_a_non_string()
    {
        NodeJson.RequireStringValue(El("\"hi\""), "field").ShouldBe("hi");
        ShouldBeMalformed(() => NodeJson.RequireStringValue(El("5"), "field"));
    }

    [Fact]
    public void RequireBoolValue_rejects_a_non_bool()
    {
        NodeJson.RequireBoolValue(El("true"), "field").ShouldBeTrue();
        NodeJson.RequireBoolValue(El("false"), "field").ShouldBeFalse();
        ShouldBeMalformed(() => NodeJson.RequireBoolValue(El("\"yes\""), "field"));
    }

    [Fact]
    public void RequireObjectValue_rejects_a_non_object()
    {
        NodeJson.RequireObjectValue(El("{}"), "field").ValueKind.ShouldBe(JsonValueKind.Object);
        ShouldBeMalformed(() => NodeJson.RequireObjectValue(El("5"), "field"));
    }

    [Fact]
    public void RequireLongValue_rejects_a_non_number_or_a_non_integer()
    {
        NodeJson.RequireLongValue(El("42"), "field").ShouldBe(42L);
        ShouldBeMalformed(() => NodeJson.RequireLongValue(El("\"42\""), "field")); // not a number
        ShouldBeMalformed(() => NodeJson.RequireLongValue(El("2.5"), "field"));    // not an integer
    }
}
