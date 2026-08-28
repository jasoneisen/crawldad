namespace Crawldad.Portal.Auth;

/// <summary>The portal's production email-provider knobs, bound from <c>Crawldad:Portal:Email</c> for the boot-time
/// guard (<see cref="PostmarkEmailOptionsValidator"/>) and consumed by <see cref="PostmarkEmailSender"/>. Both
/// <see cref="ServerToken"/> and <see cref="FromAddress"/> set ⇒ the OTP mailer POSTs to Postmark in <b>any</b>
/// environment (so it can be smoke-tested locally with a real token). Both empty (the default) ⇒ no provider is wired,
/// which keeps the exact skeleton behaviour: the dev <see cref="LoggingEmailSender"/> in Development, the fail-closed
/// <see cref="UnconfiguredEmailSender"/> everywhere else. A HALF-configured pair fails fast at boot. Mirrors the shape
/// of <c>Crawldad.Portal.Infrastructure.Security.DataProtectionOptions</c> — its own portal-scoped section, all-or-nothing.</summary>
internal sealed class PostmarkEmailOptions
{
    /// <summary>The configuration section these bind from. Portal-scoped (mirrors <c>Crawldad:Portal:DataProtection</c>)
    /// so the portal's config keys never collide with the API's and the infra plumbing is self-documenting.</summary>
    public const string Section = "Crawldad:Portal:Email";

    /// <summary>Postmark <b>server</b> token (the per-server API token sent as the <c>X-Postmark-Server-Token</c>
    /// header). A secret — supplied at deploy time by reference to a Key Vault secret, never committed. Empty ⇒ the
    /// provider is off. It is never logged and never included in an exception message.</summary>
    public string ServerToken { get; init; } = "";

    /// <summary>The verified sender address every OTP email is sent <c>From</c> (its signature/domain must be verified
    /// in Postmark). Must parse as an email address when set. Empty ⇒ the provider is off.</summary>
    public string FromAddress { get; init; } = "";

    /// <summary>The Postmark message stream the OTP mail is sent on. Defaults to Postmark's built-in transactional
    /// stream, <c>outbound</c> — sign-in codes are transactional, never broadcast. Only overridden if the account uses
    /// a differently-named transactional stream.</summary>
    public string MessageStream { get; init; } = "outbound";
}
