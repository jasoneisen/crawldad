using System.Text.Json;
using Crawldad.Contracts.Payloads;
using Json.Schema;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// The JSON Schema gate (Deliverable 1/§12): validates a payload against <c>schema/crawldad-1.schema.json</c>, the
/// normative structural spec (§4-§6). The schema is embedded in this assembly and parsed once. It fixes the document
/// shape and the node vocabulary — an unknown head or a loop/forEach missing <c>maxIterations</c> fails here — so the
/// semantic pass only ever runs on structurally sound payloads. Errors are reported per (instance location, keyword).
/// </summary>
internal static class PayloadSchema
{
    private const string _resourceName = "crawldad-1.schema.json";

    private static readonly JsonSchema _schema = Load();

    private static readonly EvaluationOptions _options = new() { OutputFormat = OutputFormat.List };

    /// <summary>Validates <paramref name="payload"/> against the payload v1 schema.</summary>
    /// <param name="payload">The payload document.</param>
    /// <returns>The schema violations (empty when the payload is structurally valid).</returns>
    public static IReadOnlyList<PayloadValidationError> Validate(JsonElement payload)
    {
        var results = _schema.Evaluate(payload, _options);
        if (results.IsValid)
        {
            return [];
        }

        // OutputFormat.List flattens every failing sub-schema into Details (non-null once the root is invalid); the
        // root itself is the annotation container, so Collect it too — its Errors are null and add nothing.
        var errors = new List<PayloadValidationError>();
        Collect(results, errors);
        foreach (var detail in results.Details!)
        {
            Collect(detail, errors);
        }

        return errors;
    }

    private static void Collect(EvaluationResults node, List<PayloadValidationError> errors)
    {
        var nodeErrors = node.Errors;
        if (nodeErrors is null)
        {
            return; // an annotation-only (valid) node carries no errors.
        }

        var path = node.InstanceLocation.ToString();
        foreach (var (keyword, message) in nodeErrors)
        {
            errors.Add(new PayloadValidationError(path, keyword, message));
        }
    }

    private static JsonSchema Load()
    {
        // The schema is embedded by Crawldad.Web.csproj under this exact logical name, so the stream is always present;
        // a missing resource is a build misconfiguration that fails loudly here at first use (no coverable branch).
        using var stream = typeof(PayloadSchema).Assembly.GetManifestResourceStream(_resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}
