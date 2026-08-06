using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawldad.Contracts;

/// <summary>
/// Single source of truth for the JSON wire conventions shared by the API host and any typed client, so the
/// two stay byte-compatible when round-tripping the shared <c>Contracts</c> types. As the feature slices add
/// enums to the wire (run status, failure class), this is where those conventions accrete.
/// </summary>
public static class ContractsJson
{
    /// <summary>Applies the wire conventions shared by the API host and any typed client: enums as
    /// <b>camelCase</b> strings, so <c>RunStatus.Succeeded</c> is <c>"succeeded"</c> on the wire (§10).</summary>
    public static void Configure(JsonSerializerOptions options) =>
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
}
