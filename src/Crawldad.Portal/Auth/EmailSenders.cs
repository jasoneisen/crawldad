using Microsoft.Extensions.Logging;

namespace Crawldad.Portal.Auth;

/// <summary>Delivers a sign-in code to an email address. A real provider (SMTP/SES/…) implements this later; the
/// skeleton ships only a development logger and a fail-closed stub.</summary>
internal interface IEmailSender
{
    /// <summary>Deliver <paramref name="code"/> to <paramref name="email"/>. Implementations must either deliver
    /// or fail loudly — never silently succeed.</summary>
    Task SendOtpCodeAsync(string email, string code, CancellationToken cancellationToken);
}

/// <summary>Development-only sender: writes the code to the log at Information so a developer can complete the
/// flow without a mail provider. Registered ONLY in the Development environment — see
/// <see cref="PortalHost"/>. This is the one and only place a code is ever logged.</summary>
internal sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendOtpCodeAsync(string email, string code, CancellationToken cancellationToken)
    {
        logger.LogInformation("[dev] Portal sign-in code for {Email} is {Code}", email, code);
        return Task.CompletedTask;
    }
}

/// <summary>The fail-closed sender used in every non-Development environment until a real provider is wired.
/// It refuses to send: it must never silently succeed (a user could never sign in yet believe a code was sent)
/// and must never fall back to logging the code. Requesting a code therefore surfaces an error instead of
/// leaking one.</summary>
internal sealed class UnconfiguredEmailSender : IEmailSender
{
    public Task SendOtpCodeAsync(string email, string code, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "No email sender is configured for this environment. Refusing to issue a sign-in code (fail closed). " +
            "Configure a real IEmailSender before enabling portal sign-in outside Development.");
}
