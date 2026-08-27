using System.Text.Json;
using Crawldad.Contracts.Payloads;
using FluentValidation;

namespace Crawldad.Api.Features.Payloads;

/// <summary>Guards <c>POST /payloads/{id}/revise</c> at the boundary, mirroring <see cref="SavePayloadRequestValidator"/>:
/// the revised payload must be a JSON object. Deep structural and semantic validation runs in the endpoint via
/// <see cref="PayloadValidation"/>; this validator only rejects a grossly-shaped body first.</summary>
public sealed class RevisePayloadRequestValidator : AbstractValidator<RevisePayloadRequest>
{
    public RevisePayloadRequestValidator() =>
        RuleFor(x => x.Payload)
            .Must(static payload => payload.ValueKind == JsonValueKind.Object)
            .WithMessage("payload must be a JSON object");
}
