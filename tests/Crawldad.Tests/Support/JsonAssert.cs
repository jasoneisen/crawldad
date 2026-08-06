using System.Text.Json;

namespace Crawldad.Tests.Support;

/// <summary>JSON comparison helpers for the golden assertions: an order-insensitive structural deep-equal and an
/// order-sensitive canonical serialization (the byte compare). The gate asserts both.</summary>
internal static class JsonAssert
{
    /// <summary>Canonical serialization (no whitespace, property order preserved), for the byte compare.</summary>
    /// <param name="element">The element to serialize.</param>
    public static string Canonical(JsonElement element) => JsonSerializer.Serialize(element);

    /// <summary>Order-insensitive structural equality (objects compared as key sets, arrays in order).</summary>
    /// <param name="left">Left element.</param>
    /// <param name="right">Right element.</param>
    public static bool DeepEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                var leftProps = left.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var rightProps = right.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                return leftProps.Count == rightProps.Count
                    && leftProps.All(kvp => rightProps.TryGetValue(kvp.Key, out var other) && DeepEquals(kvp.Value, other));
            case JsonValueKind.Array:
                var leftItems = left.EnumerateArray().ToList();
                var rightItems = right.EnumerateArray().ToList();
                return leftItems.Count == rightItems.Count
                    && leftItems.Zip(rightItems, DeepEquals).All(equal => equal);
            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            default:
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
        }
    }
}
