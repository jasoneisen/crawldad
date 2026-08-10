using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawldad.Contracts;

/// <summary>Single source of truth for the JSON wire conventions shared by the API host and any typed client
/// (byte-compatible round-tripping of <c>Contracts</c> types); new wire enums register their conventions here.</summary>
public static class ContractsJson
{
    /// <summary>Registers camelCase-string enum serialization (e.g. <c>RunStatus.Succeeded</c> → <c>"succeeded"</c>).</summary>
    public static void Configure(JsonSerializerOptions options) =>
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
}
