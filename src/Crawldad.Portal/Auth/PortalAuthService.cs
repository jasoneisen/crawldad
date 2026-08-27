using Marten;

namespace Crawldad.Portal.Auth;

/// <summary>The passwordless email-OTP flow: request a code, then verify it. Owns all the policy — normalization,
/// rate limiting, expiry, attempt capping, single use, and lazy account creation.</summary>
internal interface IPortalAuthService
{
    /// <summary>Issue a fresh code for <paramref name="email"/> (unless rate limited) and hand it to the email
    /// sender. Behaves identically whether or not an account already exists, so it cannot be used to enumerate
    /// registered addresses.</summary>
    Task<RequestCodeOutcome> RequestCodeAsync(string email, CancellationToken cancellationToken);

    /// <summary>Verify <paramref name="code"/> against the most recent active challenge for
    /// <paramref name="email"/>. On success the account is upserted, its last-login stamped, and the challenge
    /// consumed.</summary>
    Task<VerifyResult> VerifyCodeAsync(string email, string code, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPortalAuthService"/>
internal sealed class PortalAuthService(
    IDocumentStore store,
    IEmailSender emailSender,
    IOtpCodeGenerator codeGenerator,
    TimeProvider clock) : IPortalAuthService
{
    /// <summary>How long a code stays valid after it is issued.</summary>
    internal static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The sliding window the per-email request cap is measured over.</summary>
    internal static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(15);

    /// <summary>Most codes an address may request within <see cref="RateLimitWindow"/>.</summary>
    internal const int MaxRequestsPerWindow = 3;

    /// <summary>Most verify attempts allowed against a single challenge.</summary>
    internal const int MaxAttemptsPerChallenge = 5;

    public async Task<RequestCodeOutcome> RequestCodeAsync(string email, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEmail(email);
        var now = clock.GetUtcNow();
        await using var session = store.LightweightSession();

        var windowStart = now - RateLimitWindow;
        var recentRequests = await session.Query<OtpChallenge>()
            .Where(c => c.Email == normalized && c.CreatedAt > windowStart)
            .CountAsync(cancellationToken);
        if (recentRequests >= MaxRequestsPerWindow)
        {
            return RequestCodeOutcome.RateLimited;
        }

        var code = codeGenerator.Generate();
        var (hash, salt) = OtpHasher.Hash(code);
        session.Store(new OtpChallenge
        {
            Id = Guid.NewGuid(),
            Email = normalized,
            CodeHash = hash,
            Salt = salt,
            CreatedAt = now,
            ExpiresAt = now + CodeLifetime,
        });
        await session.SaveChangesAsync(cancellationToken);

        // A challenge is created and "sent" for EVERY address, whether or not a PortalUser exists — the observable
        // behaviour is identical, so an attacker cannot tell registered addresses from unregistered ones.
        await emailSender.SendOtpCodeAsync(normalized, code, cancellationToken);
        return RequestCodeOutcome.Sent;
    }

    public async Task<VerifyResult> VerifyCodeAsync(string email, string code, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEmail(email);
        var entered = NormalizeCode(code);
        var now = clock.GetUtcNow();
        await using var session = store.LightweightSession();

        // Verify against the newest still-open challenge for this address (a user may hold up to
        // MaxRequestsPerWindow codes; the latest email is the one to enter). Attempts are counted per challenge.
        var challenge = await session.Query<OtpChallenge>()
            .Where(c => c.Email == normalized && !c.Consumed)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (challenge is null)
        {
            return VerifyResult.Fail(VerifyOutcome.NoActiveChallenge, normalized);
        }
        if (challenge.ExpiresAt <= now)
        {
            return VerifyResult.Fail(VerifyOutcome.Expired, normalized);
        }
        if (challenge.AttemptCount >= MaxAttemptsPerChallenge)
        {
            return VerifyResult.Fail(VerifyOutcome.TooManyAttempts, normalized);
        }

        challenge.AttemptCount++;
        if (!OtpHasher.Verify(entered, challenge.Salt, challenge.CodeHash))
        {
            session.Store(challenge);
            await session.SaveChangesAsync(cancellationToken);
            return VerifyResult.Fail(VerifyOutcome.InvalidCode, normalized);
        }

        challenge.Consumed = true;
        session.Store(challenge);

        var user = await session.LoadAsync<PortalUser>(normalized, cancellationToken)
            ?? new PortalUser { Email = normalized, CreatedAt = now };
        user.LastLoginAt = now;
        session.Store(user);
        await session.SaveChangesAsync(cancellationToken);

        return new VerifyResult(VerifyOutcome.Success, normalized, user.DisplayName);
    }

    /// <summary>Case- and whitespace-normalize an email to its canonical stored form.</summary>
    internal static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>Normalize a typed code to the generator's alphabet casing so lower-case entry still matches.</summary>
    internal static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();
}
