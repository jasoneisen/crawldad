using System.Text.Json;

namespace Crawldad.Api.Features.Runs.Interpreter;

/// <summary>Classified typed reads of run-payload JSON. An inline <c>POST /runs</c> payload is NOT schema-validated
/// (only the structural pre-pass runs), so a field of the wrong JSON kind — or a missing required one — must terminate
/// as a <c>malformed_node</c> <see cref="InterpreterException"/>, never a raw accessor's uncaught exception (a 500).</summary>
internal static class NodeJson
{
    // Every field-form read assumes an object body — an invariant the structural pre-pass (node bodies, config) or a
    // prior RequireObject (nested objects) already established; TryGetProperty would itself throw on a non-object.

    /// <summary>A required string field.</summary>
    public static string RequireString(JsonElement body, string field) =>
        body.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Malformed($"'{field}' must be a string");

    /// <summary>An optional string field: absent ⇒ null, present-but-not-a-string ⇒ malformed.</summary>
    public static string? OptionalString(JsonElement body, string field) =>
        !body.TryGetProperty(field, out var value) ? null
            : value.ValueKind == JsonValueKind.String ? value.GetString()
            : throw Malformed($"'{field}' must be a string");

    /// <summary>An optional bool field: absent ⇒ default, present-but-not-a-bool ⇒ malformed.</summary>
    public static bool OptionalBool(JsonElement body, string field, bool @default) =>
        !body.TryGetProperty(field, out var value) ? @default : RequireBoolValue(value, $"'{field}'");

    /// <summary>An optional 32-bit-integer field: absent ⇒ default, present-but-not-an-integer ⇒ malformed.</summary>
    public static int OptionalInt(JsonElement body, string field, int @default) =>
        !body.TryGetProperty(field, out var value) ? @default
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number
            : throw Malformed($"'{field}' must be an integer");

    /// <summary>A required object field.</summary>
    public static JsonElement RequireObject(JsonElement body, string field) =>
        body.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw Malformed($"'{field}' must be an object");

    /// <summary>An optional object field: absent ⇒ null, present-but-not-an-object ⇒ malformed.</summary>
    public static JsonElement? OptionalObject(JsonElement body, string field) =>
        !body.TryGetProperty(field, out var value) ? null
            : value.ValueKind == JsonValueKind.Object ? value
            : throw Malformed($"'{field}' must be an object");

    /// <summary>An optional array-of-strings field: absent ⇒ empty, a non-array or a non-string element ⇒ malformed.</summary>
    public static IReadOnlyList<string> OptionalStringArray(JsonElement body, string field)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Malformed($"'{field}' must be an array");
        }

        var items = new List<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            items.Add(RequireStringValue(item, $"'{field}' element"));
        }

        return items;
    }

    /// <summary>A required field of any kind — its presence classified, its kind checked by the caller (e.g. a selector
    /// or a block whose array-ness the structural pre-pass already guaranteed).</summary>
    public static JsonElement RequireElement(JsonElement body, string field) =>
        body.TryGetProperty(field, out var value) ? value : throw Malformed($"'{field}' is required");

    /// <summary>An already-extracted value that must be a string.</summary>
    public static string RequireStringValue(JsonElement value, string what) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Malformed($"{what} must be a string");

    /// <summary>An already-extracted value that must be a bool.</summary>
    public static bool RequireBoolValue(JsonElement value, string what) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw Malformed($"{what} must be a boolean");

    /// <summary>An already-extracted value that must be an object.</summary>
    public static JsonElement RequireObjectValue(JsonElement value, string what) =>
        value.ValueKind == JsonValueKind.Object ? value : throw Malformed($"{what} must be an object");

    /// <summary>An already-extracted value that must be an integer the 64-bit loop counter can take.</summary>
    public static long RequireLongValue(JsonElement value, string what) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : throw Malformed($"{what} must be an integer");

    private static InterpreterException Malformed(string message) => new(InterpreterErrorCodes.MalformedNode, message);
}
