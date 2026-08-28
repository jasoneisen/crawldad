using JasperFx;
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

        // Send BEFORE persisting: a fail-closed sender (non-dev, unconfigured) throws here and leaves no orphan
        // challenge row behind. A challenge is created and "sent" for EVERY address, whether or not a PortalUser
        // exists — identical observable behaviour, so registered addresses can't be enumerated.
        await emailSender.SendOtpCodeAsync(normalized, code, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return RequestCodeOutcome.Sent;
    }

    public async Task<VerifyResult> VerifyCodeAsync(string email, string code, CancellationToken cancellationToken)
    {
        var normalized = NormalizeEmail(email);
        var entered = NormalizeCode(code);

        // OtpChallenge is stored with optimistic concurrency, so a verify attempt whose increment loses a race
        // throws ConcurrencyException instead of silently last-write-wins. Retry against the fresh version: the
        // attempt cap is re-checked each pass, so parallel guesses can never exceed it. This terminates — once the
        // cap is reached the reject path saves nothing and cannot conflict.
        while (true)
        {
            try
            {
                return await VerifyOnceAsync(normalized, entered, cancellationToken);
            }
            catch (ConcurrencyException)
            {
                // Lost the race for this challenge's version; reload and re-evaluate.
            }
        }
    }

    private async Task<VerifyResult> VerifyOnceAsync(string normalized, string entered, CancellationToken cancellationToken)
    {
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
        var matched = OtpHasher.Verify(entered, challenge.Salt, challenge.CodeHash);

        PortalUser? user = null;
        if (matched)
        {
            challenge.Consumed = true;
            user = await session.LoadAsync<PortalUser>(normalized, cancellationToken)
                ?? new PortalUser { Email = normalized, CreatedAt = now };
            user.LastLoginAt = now;
            session.Store(user);
        }

        session.Store(challenge);

        // Test seam (null in production): lets a test run between the load above and this save to force a real
        // ConcurrencyException, so the retry path in VerifyCodeAsync is covered deterministically.
        if (ConcurrencyProbe is not null)
        {
            await ConcurrencyProbe(cancellationToken);
        }

        await session.SaveChangesAsync(cancellationToken);

        return matched
            ? new VerifyResult(VerifyOutcome.Success, normalized, user!.DisplayName)
            : VerifyResult.Fail(VerifyOutcome.InvalidCode, normalized);
    }

    /// <summary>Test-only seam — see <see cref="VerifyOnceAsync"/>. Null in production.</summary>
    internal Func<CancellationToken, Task>? ConcurrencyProbe { get; set; }

    /// <summary>Case- and whitespace-normalize an email to its canonical stored form. Delegates to the single shared
    /// normalizer (issue #119 PR4, finding #2) so the portal and the API's membership store fold identity byte-for-byte the
    /// same way — the historical <c>Trim().ToLowerInvariant()</c> behaviour, unchanged.</summary>
    internal static string NormalizeEmail(string email) => Crawldad.Contracts.EmailAddress.Normalize(email);

    /// <summary>Normalize a typed code to the generator's alphabet casing so lower-case entry still matches.</summary>
    internal static string NormalizeCode(string code) => code.Trim().ToUpperInvariant();
}
