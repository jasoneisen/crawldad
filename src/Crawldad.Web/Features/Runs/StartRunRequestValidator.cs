using System.Text.Json;
using Crawldad.Contracts.Runs;
using FluentValidation;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// Guards <c>POST /runs</c> at the boundary (§12/§14.2): a run supplies exactly one payload source — an inline
/// <c>payload</c> object <b>xor</b> a pinned <c>payloadId</c> — and inputs must be a JSON object when present. A failure
/// is a 400 ProblemDetails via the shared FluentValidation middleware. Deeper structural/semantic checks of an inline
/// payload surface at execution as a typed run failure; a pinned payload was already validated at save (§12).
/// </summary>
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

    // Exactly one of an inline payload object / a pinned payloadId — the two are mutually exclusive (§14.2).
    private static bool HasExactlyOnePayloadSource(StartRunRequest request) =>
        (request.Payload.ValueKind == JsonValueKind.Object) != request.PayloadId.HasValue;
}
