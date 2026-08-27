namespace Crawldad.Portal.Auth;

/// <summary>A portal account, stored as a Marten document in the "portal" schema. The email is the document
/// identity (configured via <c>Identity(u =&gt; u.Email)</c>) so it is unique by construction; it is always stored
/// case-normalized (lower-invariant), which is what makes the uniqueness case-insensitive. Accounts are created
/// lazily on the first successful OTP sign-in — there is no separate registration step.</summary>
public sealed class PortalUser
{
    /// <summary>The account's email — the document id, always normalized to lower-invariant. Unique.</summary>
    public string Email { get; set; } = "";

    /// <summary>Optional display name. Null until the user sets one (no UI for it in the skeleton).</summary>
    public string? DisplayName { get; set; }

    /// <summary>When the account was first created (its first successful sign-in). Preserved across logins.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the account last signed in. Stamped on every successful verification.</summary>
    public DateTimeOffset LastLoginAt { get; set; }
}
