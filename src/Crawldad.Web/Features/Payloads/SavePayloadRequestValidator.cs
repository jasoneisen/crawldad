using System.Text.Json;
using Crawldad.Contracts.Payloads;
using FluentValidation;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// Guards <c>POST /payloads</c> at the boundary (§12), mirroring <c>StartRunRequestValidator</c>: the payload must be a
/// JSON object. A failure is a 400 ProblemDetails via the shared FluentValidation middleware. The deep structural and
/// semantic validation (JSON Schema + defined-before-use + expression parse/arity) runs in the endpoint and produces
/// the structured error list — this validator only rejects a grossly-shaped body before that work begins.
/// </summary>
public sealed class SavePayloadRequestValidator : AbstractValidator<SavePayloadRequest>
{
    /// <summary>Wires the boundary rule.</summary>
    public SavePayloadRequestValidator() =>
        RuleFor(x => x.Payload)
            .Must(static payload => payload.ValueKind == JsonValueKind.Object)
            .WithMessage("payload must be a JSON object");
}
