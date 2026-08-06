using System.Text.Json;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>
/// Bridges System.Text.Json and the run value model (§7.1: null|bool|long|double|string|array|map|handle). Inputs and
/// literal <c>vars</c> come in as JSON; the payload's evaluated <c>result</c> goes out as JSON with object-literal key
/// order preserved (maps are insertion-ordered). Opaque handles are never serialisable — a handle anywhere in the
/// result tree is a terminal <c>handle_in_result</c> failure.
/// </summary>
internal static class JsonValues
{
    /// <summary>Converts a JSON element to a value-model value (numbers: integral ⇒ <see cref="long"/>, else <see cref="double"/>).</summary>
    /// <param name="element">The JSON element (from request inputs or a literal <c>var</c>).</param>
    /// <returns>The value-model value.</returns>
    public static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ObjectFromJson(element),
        JsonValueKind.Array => ArrayFromJson(element),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null, // Null + Undefined (an absent optional input) both map to the value-model null.
    };

    private static Dictionary<string, object?> ObjectFromJson(JsonElement element)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            map[property.Name] = FromJson(property.Value);
        }

        return map;
    }

    private static List<object?> ArrayFromJson(JsonElement element)
    {
        var list = new List<object?>();
        foreach (var item in element.EnumerateArray())
        {
            list.Add(FromJson(item));
        }

        return list;
    }

    /// <summary>Serialises an evaluated result value to a <see cref="JsonElement"/>, preserving map key order.</summary>
    /// <param name="value">The value-model value produced by the payload's <c>result</c> expression.</param>
    /// <returns>The JSON element to embed in the response.</returns>
    /// <exception cref="InterpreterException">On an opaque handle anywhere in the tree (<c>handle_in_result</c>).</exception>
    public static JsonElement ToJson(object? value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }

        // Parse back into an owned element so it survives the writer/buffer going out of scope.
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case List<object?> list:
                writer.WriteStartArray();
                foreach (var item in list)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            case Dictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (key, item) in map)
                {
                    writer.WritePropertyName(key);
                    Write(writer, item);
                }

                writer.WriteEndObject();
                break;
            default:
                throw new InterpreterException(
                    InterpreterErrorCodes.HandleInResult,
                    $"the result contains a non-serialisable {ExpressionValues.TypeName(value)} (a bound locator/frame handle cannot cross the wire)");
        }
    }
}
