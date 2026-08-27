using System.Text.Json;
using Crawldad.Contracts.Runs;
using FluentValidation;

namespace Crawldad.Api.Features.Runs;

/// <summary>Guards <c>POST /runs</c> at the boundary: a run supplies exactly one payload source — an inline
/// <c>payload</c> object <b>xor</b> a pinned <c>payloadId</c> — and inputs must be a JSON object when present. Deeper
/// structural checks of an inline payload surface at execution as a typed run failure; a pinned payload was already validated at save.</summary>
public sealed class StartRunRequestValidator : AbstractValidator<StartRunRequest>
{
    /// <summary>Wires the boundary rules.</summary>
    public StartRunRequestValidator()
    {
        RuleFor(x => x.PayloadId)
            .Must(static (request, _) => HasExactlyOnePayloadSource(request))
            .WithMessage("provide exactly one of payload (inline) or payloadId (pinned)");

        RuleFor(x => x.Inputs)
            .Must(static inputs => inputs.ValueKind is JsonValueKind.Object or JsonValueKind.Undefined)
            .WithMessage("inputs must be a JSON object when present");
    }

    // Exactly one of an inline payload object / a pinned payloadId — the two are mutually exclusive.
    private static bool HasExactlyOnePayloadSource(StartRunRequest request) =>
        (request.Payload.ValueKind == JsonValueKind.Object) != request.PayloadId.HasValue;
}
