using System.Text.Json;
using Crawldad.Contracts;

namespace Crawldad.Client;

/// <summary>The JSON conventions the client shares with the API host: Web defaults (camelCase properties) plus the
/// camelCase string-enum convention registered by <see cref="ContractsJson"/>, so every <c>Crawldad.Contracts</c>
/// type round-trips byte-for-byte against the server. One shared, immutable options instance for the whole client.</summary>
internal static class CrawldadJson
{
    /// <summary>The single serializer options instance used for every request body and response read.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ContractsJson.Configure(options);
        return options; // STJ freezes the options on first (de)serialize; the single shared instance is never mutated after this
    }
}
