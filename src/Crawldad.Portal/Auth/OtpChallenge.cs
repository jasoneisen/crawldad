namespace Crawldad.Portal.Auth;

/// <summary>A pending email one-time-passcode challenge, stored as a Marten document in the "portal" schema. The
/// code itself is NEVER stored — only a salted SHA-256 hash of it (<see cref="CodeHash"/> + <see cref="Salt"/>),
/// compared in constant time at verification. A challenge is single-use (<see cref="Consumed"/>), time-boxed
/// (<see cref="ExpiresAt"/>), and attempt-capped (<see cref="AttemptCount"/>).</summary>
public sealed class OtpChallenge
{
    /// <summary>Surrogate document id (a fresh Guid per request).</summary>
    public Guid Id { get; set; }

    /// <summary>The target email, normalized to lower-invariant (matches <see cref="PortalUser.Email"/>).</summary>
    public string Email { get; set; } = "";

    /// <summary>Base64 SHA-256 of <see cref="Salt"/> ++ the code. The plaintext code is never persisted.</summary>
    public string CodeHash { get; set; } = "";

    /// <summary>Base64 per-challenge random salt folded into <see cref="CodeHash"/>.</summary>
    public string Salt { get; set; } = "";

    /// <summary>When the challenge was created (used for the per-email request rate limit window).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the code stops being valid (creation + 10 minutes).</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>How many verification attempts have been made against this challenge (capped at 5).</summary>
    public int AttemptCount { get; set; }

    /// <summary>Set once the challenge is successfully verified — enforces single use.</summary>
    public bool Consumed { get; set; }
}
