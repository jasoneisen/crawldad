using Crawldad.Contracts.Payloads;
using FluentValidation;

namespace Crawldad.Web.Features.Payloads;

/// <summary>
/// Guards <c>POST /payloads/{id}/rename</c> at the boundary (§14.1): the new name must be non-empty (matching the
/// schema's <c>name</c> <c>minLength:1</c>). A failure is a 400 ProblemDetails via the shared FluentValidation middleware.
/// </summary>
public sealed class RenamePayloadRequestValidator : AbstractValidator<RenamePayloadRequest>
{
    /// <summary>Wires the boundary rule.</summary>
    public RenamePayloadRequestValidator() =>
        RuleFor(x => x.Name)
            .Must(static name => !string.IsNullOrWhiteSpace(name))
            .WithMessage("name must not be empty");
}
