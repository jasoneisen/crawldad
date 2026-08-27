namespace Crawldad.Portal.Auth;

/// <summary>User-facing copy for the sign-in flow, kept out of the page so each branch is unit-testable. The
/// messages are deliberately identical for existing and unknown addresses (no account enumeration).</summary>
internal static class OtpMessages
{
    /// <summary>Status line shown after a code request. <paramref name="email"/> is the normalized address.</summary>
    internal static string DescribeRequest(RequestCodeOutcome outcome, string email) => outcome switch
    {
        RequestCodeOutcome.Sent => $"If {email} can receive mail, a 6-character sign-in code is on its way. Enter it below.",
        _ => "You've requested several codes recently. Check your inbox for the most recent one, or try again in a few minutes.",
    };

    /// <summary>Error line shown after a failed verify. Only ever called for the non-success outcomes; the
    /// discard arm carries <see cref="VerifyOutcome.NoActiveChallenge"/>.</summary>
    internal static string DescribeFailure(VerifyOutcome outcome) => outcome switch
    {
        VerifyOutcome.InvalidCode => "That code isn't right. Double-check it and try again.",
        VerifyOutcome.Expired => "That code has expired. Request a fresh one.",
        VerifyOutcome.TooManyAttempts => "Too many attempts for that code. Request a fresh one.",
        _ => "We don't have a pending code for that address. Request a new one.",
    };
}
