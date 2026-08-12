using Crawldad.Contracts.Webhooks;
using FluentValidation;

namespace Crawldad.Web.Features.Webhooks;

/// <summary>Boundary validation for <c>PUT /webhooks/{name}</c>: the target URL passes the SSRF policy (https, no
/// loopback/link-local/private address), the signing secret is present and long enough to be a real HMAC key, and any
/// subscribed event types are from the catalog. The name (route key) is guarded in the endpoint, not here — FluentValidation
/// only sees the body.</summary>
public sealed class RegisterWebhookRequestValidator : AbstractValidator<RegisterWebhookRequest>
{
    /// <summary>The minimum signing-secret length — long enough that an HMAC key carries real entropy, and above the
    /// credential scrubber's exact-match floor.</summary>
    public const int MinSecretLength = 16;

    /// <summary>Builds the register-request rules.</summary>
    public RegisterWebhookRequestValidator()
    {
        RuleFor(static x => x.Url).Custom(static (url, context) =>
        {
            if (string.IsNullOrEmpty(url))
            {
                context.AddFailure(nameof(RegisterWebhookRequest.Url), "url must not be empty");
            }
            else if (!WebhookUrlPolicy.IsAllowed(url, out var reason))
            {
                context.AddFailure(nameof(RegisterWebhookRequest.Url), reason);
            }
        });

        RuleFor(static x => x.Secret)
            .NotEmpty()
            .WithMessage("secret must not be empty");

        RuleFor(static x => x.Secret)
            .Must(static secret => secret.Length >= MinSecretLength)
            .When(static x => !string.IsNullOrEmpty(x.Secret))
            .WithMessage($"secret must be at least {MinSecretLength} characters");

        RuleFor(static x => x.Events)
            .Must(static events => events is null || events.All(WebhookEventTypes.IsKnown))
            .WithMessage($"events must be a subset of: {WebhookEventTypes.Catalog}");
    }
}
