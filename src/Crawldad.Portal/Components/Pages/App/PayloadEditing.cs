using System.Text.Json;
using Crawldad.Contracts.Payloads;

namespace Crawldad.Portal.Components.Pages.App;

/// <summary>Shared, side-effect-free helpers for the payload-editor forms. The portal's only local check is JSON
/// well-formedness (it must parse the text to build any request anyway); the authoritative crawldad-DSL validation —
/// JSON Schema plus the semantic pass — runs server-side on save/revise, and its <see cref="PayloadValidationError"/>
/// list is rendered verbatim. There is no server-side dry-run endpoint, so "Validate" is exactly this well-formedness
/// pre-flight.</summary>
internal static class PayloadEditing
{
    /// <summary>The submit intent that persists via the API. Any other value (including unset) validates only.</summary>
    public const string SaveIntent = "save";

    /// <summary>The stable problem code the portal stamps on a body that is not well-formed JSON (never reaches the API).</summary>
    public const string InvalidJsonCode = "invalid_json";

    /// <summary>Attempts to parse <paramref name="script"/> into a standalone (cloned) <see cref="JsonElement"/> that
    /// outlives the parse. On failure (empty or malformed), yields a single synthetic <see cref="PayloadValidationError"/>
    /// in the same shape the API uses, so a bad paste renders through the identical problem surface as a server rejection.</summary>
    /// <param name="script">The raw JSON text from the editor.</param>
    /// <param name="element">The parsed, cloned document root when well-formed; otherwise <c>default</c>.</param>
    /// <param name="error">The synthetic <c>invalid_json</c> problem when empty/malformed; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when the text is well-formed JSON.</returns>
    public static bool TryParse(string? script, out JsonElement element, out PayloadValidationError? error)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            element = default;
            error = new PayloadValidationError("", InvalidJsonCode, "The editor is empty — paste a crawldad payload document.");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(script);
            element = document.RootElement.Clone();
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            element = default;
            error = new PayloadValidationError("", InvalidJsonCode, $"The editor does not contain valid JSON: {ex.Message}");
            return false;
        }
    }
}
