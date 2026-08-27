using Crawldad.Portal.Auth;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Portal;

/// <summary>Exercises the OTP policy directly against the real Marten store (the fixture's fake clock + capturing
/// sender). Each test uses a unique, mixed-case email so it is isolated and also checks normalization; the clock
/// only ever moves forward.</summary>
[Collection(PortalCollection.Name)]
public class PortalAuthServiceTests(PortalFixture fixture)
{
    private static string NewEmail() => $"Svc-{Guid.NewGuid():N}@Example.COM";

    private static IPortalAuthService NewService(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IPortalAuthService>();

    [Fact]
    public async Task Request_then_verify_creates_the_account_and_succeeds()
    {
        var email = NewEmail();
        var normalized = PortalAuthService.NormalizeEmail(email);
        using var scope = fixture.App.Services.CreateScope();
        var auth = NewService(scope);

        (await auth.RequestCodeAsync(email, CancellationToken.None)).ShouldBe(RequestCodeOutcome.Sent);
        var code = fixture.App.Email.LastCodeFor(normalized);

        // Lower-case entry must still match (code normalization), and the email is normalized.
        var result = await auth.VerifyCodeAsync(email, code.ToLowerInvariant(), CancellationToken.None);

        result.Outcome.ShouldBe(VerifyOutcome.Success);
        result.Email.ShouldBe(normalized);

        await using var session = fixture.App.Store.QuerySession();
        var user = await session.LoadAsync<PortalUser>(normalized);
        user.ShouldNotBeNull();
        user!.LastLoginAt.ShouldBe(fixture.App.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Second_login_updates_the_existing_account()
    {
        var email = NewEmail();
        var normalized = PortalAuthService.NormalizeEmail(email);
        using var scope = fixture.App.Services.CreateScope();
        var auth = NewService(scope);

        await auth.RequestCodeAsync(email, CancellationToken.None);
        await auth.VerifyCodeAsync(email, fixture.App.Email.LastCodeFor(normalized), CancellationToken.None);
        var createdAt = (await LoadUserAsync(normalized))!.CreatedAt;

        fixture.App.Clock.Advance(TimeSpan.FromMinutes(30));
        await auth.RequestCodeAsync(email, CancellationToken.None);
        await auth.VerifyCodeAsync(email, fixture.App.Email.LastCodeFor(normalized), CancellationToken.None);

        var user = await LoadUserAsync(normalized);
        user!.CreatedAt.ShouldBe(createdAt);                 // preserved
        user.LastLoginAt.ShouldBe(fixture.App.Clock.GetUtcNow()); // advanced
    }

    [Fact]
    public async Task Wrong_code_is_rejected_and_counts_an_attempt()
    {
        var email = NewEmail();
        var normalized = PortalAuthService.NormalizeEmail(email);
        using var scope = fixture.App.Services.CreateScope();
        var auth = NewService(scope);

        await auth.RequestCodeAsync(email, CancellationToken.None);
        var result = await auth.VerifyCodeAsync(email, "WRONG9", CancellationToken.None);

        result.Outcome.ShouldBe(VerifyOutcome.InvalidCode);
        (await LoadChallengeAsync(normalized))!.AttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task Sixth_attempt_is_locked_out()
    {
        var email = NewEmail();
        using var scope = fixture.App.Services.CreateScope();
        var auth = NewService(scope);

        await auth.RequestCodeAsync(email, CancellationToken.None);
        for (var i = 0; i < PortalAuthService.MaxAttemptsPerChallenge; i++)
        {
            (await auth.VerifyCodeAsync(email, "WRONG9", CancellationToken.None)).Outcome
                .ShouldBe(VerifyOutcome.InvalidCode);
        }

        (await auth.VerifyCodeAsync(email, "WRONG9", CancellationToken.None)).Outcome
            .ShouldBe(VerifyOutcome.TooManyAttempts);
    }

    [Fact]
    public async Task Expired_code_is_rejected()
    {
        var email = NewEmail();
        var normalized = PortalAuthService.NormalizeEmail(email);
        using var scope = fixture.App.Services.CreateScope();
        var auth = NewService(scope);

        await auth.RequestCodeAsync(email, CancellationToken.None);
        var code = fixture.App.Email.LastCodeFor(normalized);
        fixture.App.Clock.Advance(PortalAuthService.CodeLifetime + TimeSpan.FromMinutes(1));

        (await auth.VerifyCodeAsync(email, code, CancellationToken.None)).Outcome.ShouldBe(VerifyOutcome.Expired);
    }

    [Fact]
    public async Task Verifying_without_a_request_reports_no_active_challenge()
    {
        var email = NewEmail();
        using var scope = fixture.App.Services.CreateScope();
        var auth = NewService(scope);

        (await auth.VerifyCodeAsync(email, "ABC234", CancellationToken.None)).Outcome
            .ShouldBe(VerifyOutcome.NoActiveChallenge);
    }

    [Fact]
    public async Task A_consumed_code_cannot_be_reused()
    {
        var email = NewEmail();
        var normalized = PortalAuthService.NormalizeEmail(email);
        using var scope = fixture.App.Services.CreateScope();
        var auth = NewService(scope);

        await auth.RequestCodeAsync(email, CancellationToken.None);
        var code = fixture.App.Email.LastCodeFor(normalized);

        (await auth.VerifyCodeAsync(email, code, CancellationToken.None)).Outcome.ShouldBe(VerifyOutcome.Success);
        // Single use: the consumed challenge is no longer active.
        (await auth.VerifyCodeAsync(email, code, CancellationToken.None)).Outcome
            .ShouldBe(VerifyOutcome.NoActiveChallenge);
    }

    [Fact]
    public async Task Fourth_request_within_the_window_is_rate_limited()
    {
        var email = NewEmail();
        using var scope = fixture.App.Services.CreateScope();
        var auth = NewService(scope);

        for (var i = 0; i < PortalAuthService.MaxRequestsPerWindow; i++)
        {
            (await auth.RequestCodeAsync(email, CancellationToken.None)).ShouldBe(RequestCodeOutcome.Sent);
        }

        (await auth.RequestCodeAsync(email, CancellationToken.None)).ShouldBe(RequestCodeOutcome.RateLimited);
    }

    [Fact]
    public async Task A_racing_increment_is_serialized_not_lost()
    {
        var email = NewEmail();
        var normalized = PortalAuthService.NormalizeEmail(email);
        var service = NewDirectService();
        await service.RequestCodeAsync(email, CancellationToken.None);
        var code = fixture.App.Email.LastCodeFor(normalized);

        // Force one real version conflict between the verify's load and its save: an out-of-band attempt bumps the
        // same challenge first, so the in-flight save must fail with ConcurrencyException and retry.
        service.ConcurrencyProbe = async ct =>
        {
            service.ConcurrencyProbe = null; // fire exactly once
            await using var s = fixture.App.Store.LightweightSession();
            var ch = await s.Query<OtpChallenge>().Where(c => c.Email == normalized)
                .OrderByDescending(c => c.CreatedAt).FirstAsync(ct);
            ch.AttemptCount++;
            s.Store(ch);
            await s.SaveChangesAsync(ct);
        };

        var result = await service.VerifyCodeAsync(email, code, CancellationToken.None);

        result.Outcome.ShouldBe(VerifyOutcome.Success);
        var challenge = await LoadChallengeAsync(normalized);
        // Both increments survived (out-of-band +1, then the retried verify +1) — no lost update under optimistic
        // concurrency; last-write-wins would have left this at 1.
        challenge!.AttemptCount.ShouldBe(2);
        challenge.Consumed.ShouldBeTrue();
    }

    [Fact]
    public async Task Parallel_wrong_guesses_cannot_exceed_the_attempt_cap()
    {
        var email = NewEmail();
        var normalized = PortalAuthService.NormalizeEmail(email);
        var service = NewDirectService();
        await service.RequestCodeAsync(email, CancellationToken.None);

        const int parallelAttempts = 8;
        var results = await Task.WhenAll(Enumerable.Range(0, parallelAttempts)
            .Select(_ => service.VerifyCodeAsync(email, "WRONG9", CancellationToken.None)));

        // Exactly the cap's worth of guesses are counted; the rest are turned away. Optimistic concurrency makes
        // the increments serialize, so the cap holds under real parallelism.
        results.Count(r => r.Outcome == VerifyOutcome.InvalidCode).ShouldBe(PortalAuthService.MaxAttemptsPerChallenge);
        results.Count(r => r.Outcome == VerifyOutcome.TooManyAttempts)
            .ShouldBe(parallelAttempts - PortalAuthService.MaxAttemptsPerChallenge);
        (await LoadChallengeAsync(normalized))!.AttemptCount.ShouldBe(PortalAuthService.MaxAttemptsPerChallenge);
    }

    private PortalAuthService NewDirectService() =>
        new(fixture.App.Store, fixture.App.Email, new OtpCodeGenerator(), fixture.App.Clock);

    private async Task<PortalUser?> LoadUserAsync(string normalizedEmail)
    {
        await using var session = fixture.App.Store.QuerySession();
        return await session.LoadAsync<PortalUser>(normalizedEmail);
    }

    private async Task<OtpChallenge?> LoadChallengeAsync(string normalizedEmail)
    {
        await using var session = fixture.App.Store.QuerySession();
        return await session.Query<OtpChallenge>()
            .Where(c => c.Email == normalizedEmail)
            .FirstOrDefaultAsync();
    }
}
