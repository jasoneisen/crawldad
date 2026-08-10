using System.Text.Json;
using Crawldad.Contracts.Payloads;
using FluentValidation;

namespace Crawldad.Web.Features.Payloads;

/// <summary>Guards <c>POST /payloads</c> at the boundary, mirroring <c>StartRunRequestValidator</c>: the payload must be
/// a JSON object (failure is a 400 ProblemDetails via the shared FluentValidation middleware). Deep structural and
/// semantic validation runs in the endpoint; this validator only rejects a grossly-shaped body first.</summary>
public sealed class SavePayloadRequestValidator : AbstractValidator<SavePayloadRequest>
{
    public SavePayloadRequestValidator() =>
        RuleFor(x => x.Payload)
            .Must(static payload => payload.ValueKind == JsonValueKind.Object)
            .WithMessage("payload must be a JSON object");
}
