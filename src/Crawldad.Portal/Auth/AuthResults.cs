namespace Crawldad.Portal.Auth;

/// <summary>The outcome of requesting a sign-in code.</summary>
internal enum RequestCodeOutcome
{
    /// <summary>A code was generated and handed to the email sender.</summary>
    Sent,

    /// <summary>The per-email rate limit was hit; no new code was issued.</summary>
    RateLimited,
}

/// <summary>The outcome of verifying a submitted code.</summary>
internal enum VerifyOutcome
{
    /// <summary>The code matched an active challenge; the user is signed in.</summary>
    Success,

    /// <summary>A challenge existed but the code did not match.</summary>
    InvalidCode,

    /// <summary>The most recent challenge has expired.</summary>
    Expired,

    /// <summary>No unconsumed challenge exists for the address.</summary>
    NoActiveChallenge,

    /// <summary>The challenge's attempt cap has been reached.</summary>
    TooManyAttempts,
}

/// <summary>The result of a verify attempt. On success it carries the account identity used to build the cookie
/// principal.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Email">The normalized email the attempt was for.</param>
/// <param name="DisplayName">The account display name, when known (success only).</param>
internal sealed record VerifyResult(VerifyOutcome Outcome, string Email, string? DisplayName)
{
    internal static VerifyResult Fail(VerifyOutcome outcome, string email) => new(outcome, email, DisplayName: null);
}
