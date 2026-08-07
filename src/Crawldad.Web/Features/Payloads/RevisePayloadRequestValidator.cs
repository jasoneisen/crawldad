using System.Text.Json;
using Crawldad.Contracts.Payloads;
using FluentValidation;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// Guards <c>POST /payloads/{id}/revise</c> at the boundary (§12), mirroring <see cref="SavePayloadRequestValidator"/>:
/// the revised payload must be a JSON object. The deep structural and semantic validation runs in the endpoint (via
/// <see cref="PayloadValidation"/>); this validator only rejects a grossly-shaped body before that work begins.
/// </summary>
public sealed class RevisePayloadRequestValidator : AbstractValidator<RevisePayloadRequest>
{
    /// <summary>Wires the boundary rule.</summary>
    public RevisePayloadRequestValidator() =>
        RuleFor(x => x.Payload)
            .Must(static payload => payload.ValueKind == JsonValueKind.Object)
            .WithMessage("payload must be a JSON object");
}
