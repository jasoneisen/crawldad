using System.Text.Json;
using Crawldad.Contracts.Payloads;
using Crawldad.Web.Features.Runs.Interpreter;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// The save-time validation gate shared by draft and revise (§12, Deliverable 2): both passes that guard execution, run
/// in the same order the run-time pre-pass uses so the two never diverge — (a) the JSON Schema (structure + node
/// vocabulary + the mandatory loop cap), then (b) the semantic pass (defined-before-use + expression/template/path
/// parse+arity), the same <see cref="PayloadValidator"/> the interpreter uses. The schema short-circuits so the semantic
/// pass only ever runs on a structurally sound document. A malformed payload never becomes an executable revision.
/// </summary>
internal static class PayloadValidation
{
    /// <summary>Validates a payload, returning the structured problem to surface as a <c>400</c>, or null when it is valid.</summary>
    /// <param name="payload">The payload document to validate.</param>
    /// <returns>A <see cref="PayloadValidationProblem"/> when invalid; null when valid.</returns>
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
