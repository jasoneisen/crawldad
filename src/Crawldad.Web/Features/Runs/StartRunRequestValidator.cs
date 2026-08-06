using System.Text.Json;
using Crawldad.Contracts.Runs;
using FluentValidation;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// Guards <c>POST /runs</c> at the boundary (§12): the payload must be a JSON object and inputs must be a JSON object
/// when present. A failure is a 400 ProblemDetails via the shared FluentValidation middleware. Deeper structural and
/// semantic checks (defined-before-use, arity, <c>maxIterations</c> presence) are save-time validation in Phase 3;
/// in Phase 1 a well-formed-but-nonsensical payload surfaces its fault at execution as a typed run failure.
/// </summary>
public sealed class StartRunRequestValidator : AbstractValidator<StartRunRequest>
{
    /// <summary>Wires the boundary rules.</summary>
    public StartRunRequestValidator()
    {
        RuleFor(x => x.Payload)
            .Must(static payload => payload.ValueKind == JsonValueKind.Object)
            .WithMessage("payload must be a JSON object");

        RuleFor(x => x.Inputs)
            .Must(static inputs => inputs.ValueKind is JsonValueKind.Object or JsonValueKind.Undefined)
            .WithMessage("inputs must be a JSON object when present");
    }
}
