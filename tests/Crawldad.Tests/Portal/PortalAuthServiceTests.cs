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
