using Crawldad.Api.Features.Runs.Interpreter;

namespace Crawldad.Tests.Unit;

/// <summary>The pure <c>config.retry.onPageCrashed</c> token → strategy mapping (<c>reopenPage</c>/<c>fail</c>): the
/// crash-handling analogue of <see cref="RetryBackoffTests"/>. The wiring into the retry loop (reopen vs not) is proven
/// in <see cref="RetryTests"/> against the fake crash fixture; here the mapping is asserted directly.</summary>
public class PageCrashHandlingTests
{
    [Fact]
    public void TryParse_maps_every_shipped_token()
    {
        PageCrashHandling.TryParse("reopenPage", out var reopen).ShouldBeTrue();
        reopen.ShouldBe(PageCrashHandlingStrategy.ReopenPage);
        PageCrashHandling.TryParse("fail", out var fail).ShouldBeTrue();
        fail.ShouldBe(PageCrashHandlingStrategy.Fail);
    }

    [Theory]
    [InlineData("ReopenPage")] // case-sensitive: the wire tokens are lowerCamel
    [InlineData("newContext")] // considered in design, deliberately not shipped — rejected like any unknown token
    [InlineData("restart")]
    [InlineData("")]
    public void TryParse_rejects_an_unknown_token(string token)
    {
        PageCrashHandling.TryParse(token, out var strategy).ShouldBeFalse();
        strategy.ShouldBe(PageCrashHandlingStrategy.ReopenPage); // the safe fallback the caller ignores in favour of rejecting
    }
}
