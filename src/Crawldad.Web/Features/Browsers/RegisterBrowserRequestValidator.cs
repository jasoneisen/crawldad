using Crawldad.Contracts.Browsers;
using FluentValidation;

namespace Crawldad.Web.Features.Browsers;

/// <summary>Boundary validation for <c>PUT /browsers/{name}</c>: the adapter and mode are known, the adapter has a
/// connect path for the mode (browserless is token-only — connectUrl on it is inert and rejected), the secret is
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

        // The adapter must have a connect path for the mode: browserless connects only by token, so connectUrl on it is
        // inert — it would register cleanly and then fail closed at connect. This rule trips only for that one known
        // pair, so an unknown adapter/mode still surfaces its own error above rather than a spurious mismatch here.
        RuleFor(static x => x.Mode)
            .Must(static (request, mode) => BrowserRegistrationRules.AdapterSupportsMode(request.Adapter, mode))
            .WithMessage("the browserless adapter connects only by token; register it with mode apiKey (connectUrl has no connect path there and would fail closed at connect)");

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
