using Crawldad.Contracts.Browsers;
using FluentValidation;

namespace Crawldad.Web.Features.Browsers;

/// <summary>Boundary validation for <c>PUT /browsers/{name}</c>: the adapter and mode are known, the secret is
/// non-empty, a <c>connectUrl</c> secret is wss/https-shaped, and any options carry no empty value. The name (route
/// key) is guarded in the endpoint, not here — FluentValidation only sees the body.</summary>
public sealed class RegisterBrowserRequestValidator : AbstractValidator<RegisterBrowserRequest>
{
    public RegisterBrowserRequestValidator()
    {
        RuleFor(static x => x.Adapter)
            .Must(BrowserRegistrationRules.IsKnownAdapter)
            .WithMessage("adapter must be a registerable backend (browserbase or browserless)");

        RuleFor(static x => x.Mode)
            .Must(BrowserRegistrationRules.IsKnownMode)
            .WithMessage("mode must be connectUrl or apiKey");

        RuleFor(static x => x.Secret)
            .NotEmpty()
            .WithMessage("secret must not be empty");

        RuleFor(static x => x.Secret)
            .Must(BrowserRegistrationRules.IsConnectUrlShape)
            .When(static x => string.Equals(x.Mode, BrowserRegistrationRules.ConnectUrlMode, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(x.Secret))
            .WithMessage("a connectUrl secret must start with wss:// or https://");

        RuleFor(static x => x.Options)
            .Must(static options => options is null || options.Values.All(static v => !string.IsNullOrEmpty(v)))
            .WithMessage("options values must not be empty");
    }
}
