using System.Text.Json;
using Crawldad.Contracts.Payloads;
using Json.Schema;

namespace Crawldad.Api.Features.Payloads;

/// <summary>Validates a payload against the embedded <c>schema/crawldad-1.schema.json</c>, fixing document shape and
/// node vocabulary so the semantic pass only ever runs on structurally sound payloads. Errors are reported per
/// (instance location, keyword).</summary>
internal static class PayloadSchema
{
    private const string _resourceName = "crawldad-1.schema.json";

    /// <summary>The raw payload v1 JSON Schema document — the embedded <c>crawldad-1.schema.json</c>, read once. Served
    /// verbatim at <c>GET /schema/crawldad-1.schema.json</c> so an editor or an LLM consumes exactly the normative DSL
    /// reference this save-time validator enforces (the served bytes and the validated bytes are one file).</summary>
    public static string Json { get; } = ReadEmbedded();

    private static readonly JsonSchema _schema = JsonSchema.FromText(Json);

    private static readonly EvaluationOptions _options = new() { OutputFormat = OutputFormat.List };

    /// <summary>Validates <paramref name="payload"/> against the payload v1 schema.</summary>
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

    private static string ReadEmbedded()
    {
        // The schema is embedded by Crawldad.Api.csproj under this exact logical name, so the stream is always present;
        // a missing resource is a build misconfiguration that fails loudly here at first use (no coverable branch).
        using var stream = typeof(PayloadSchema).Assembly.GetManifestResourceStream(_resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
