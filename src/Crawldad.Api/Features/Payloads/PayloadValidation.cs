using System.Text.Json;
using Crawldad.Api.Features.Runs.Interpreter;
using Crawldad.Contracts.Payloads;

namespace Crawldad.Api.Features.Payloads;

/// <summary>The save-time validation gate shared by draft and revise: runs in the same order as the run-time pre-pass so
/// the two never diverge — (a) JSON Schema (structure + node vocabulary + loop cap), then (b) the semantic pass via
/// <see cref="PayloadValidator"/>. The schema short-circuits, so semantics only run on a structurally sound document.</summary>
internal static class PayloadValidation
{
    /// <summary>Validates a payload, returning the structured problem to surface as a <c>400</c>, or null when it is valid.</summary>
    public static PayloadValidationProblem? Validate(JsonElement payload)
    {
        var schemaErrors = PayloadSchema.Validate(payload);
        if (schemaErrors.Count > 0)
        {
            return new PayloadValidationProblem(schemaErrors);
        }

        var semanticErrors = PayloadValidator.Validate(payload);
        if (semanticErrors.Count > 0)
        {
            return new PayloadValidationProblem([.. semanticErrors.Select(ToError)]);
        }

        return null;
    }

    private static PayloadValidationError ToError(PayloadIssue issue) => new(issue.Path, issue.Code, issue.Message);
}
