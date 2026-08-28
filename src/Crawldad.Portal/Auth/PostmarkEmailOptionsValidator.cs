using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Crawldad.Portal.Auth;

/// <summary>The boot-time guard for the portal email knobs (bound from <c>Crawldad:Portal:Email</c>, registered with
/// <c>ValidateOnStart</c>). It rejects a HALF-configured provider — a <see cref="PostmarkEmailOptions.ServerToken"/>
/// without a <see cref="PostmarkEmailOptions.FromAddress"/> or vice versa — because that is neither the fail-closed
/// skeleton nor a working provider: it would silently fall back to <see cref="UnconfiguredEmailSender"/> while looking
/// configured. When the provider IS configured (both set) the from-address must parse as an email and the message
/// stream must be non-blank. Mirrors <c>Crawldad.Portal.Infrastructure.Security.DataProtectionOptionsValidator</c>'s
/// all-or-nothing shape.</summary>
internal sealed class PostmarkEmailOptionsValidator : IValidateOptions<PostmarkEmailOptions>
{
    /// <summary>Validates the bound email knobs, collecting every failure so a misconfigured host reports them together.</summary>
    public ValidateOptionsResult Validate(string? name, PostmarkEmailOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var hasToken = !string.IsNullOrWhiteSpace(options.ServerToken);
        var hasFrom = !string.IsNullOrWhiteSpace(options.FromAddress);
        var failures = new List<string>();

        if (hasToken != hasFrom)
        {
            failures.Add("Crawldad:Portal:Email needs BOTH ServerToken and FromAddress set (or neither)");
        }

        // A configured provider (both set) must be able to build a valid Postmark request: a parseable From and a
        // non-blank stream. The token is opaque, so it is only checked for presence (Postmark rejects a bad token at
        // send time — fail-closed there, never a silent success).
        if (hasFrom && !MailAddress.TryCreate(options.FromAddress, out _))
        {
            failures.Add("Crawldad:Portal:Email:FromAddress must be a valid email address");
        }

        if (hasToken && hasFrom && string.IsNullOrWhiteSpace(options.MessageStream))
        {
            failures.Add("Crawldad:Portal:Email:MessageStream must not be blank when the provider is configured");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
