using Crawldad.Api.Infrastructure.Security;
using Crawldad.Tests.Support;
using Microsoft.Extensions.Options;

namespace Crawldad.Tests.Unit;

/// <summary>The per-<c>(email, tenant)</c> sliding-window limiter for console writes (issue #119 PR5). Driven off a fake
/// clock, so the window slides deterministically: it admits up to the limit, rejects over it, recovers exactly one window
/// after the most recent admitted write, and keeps every partition independent.</summary>
public class ConsoleWriteRateLimiterTests
{
    private static ConsoleWriteRateLimiter LimiterFor(int permitLimit, int windowSeconds, TimeProvider clock) =>
        new(Options.Create(new ConsoleWriteOptions { PermitLimit = permitLimit, WindowSeconds = windowSeconds }), clock);

    [Fact]
    public void Admits_up_to_the_limit_then_rejects()
    {
        var limiter = LimiterFor(3, 60, new AdvanceableClock(DateTimeOffset.UnixEpoch));

        limiter.TryAcquire("a@x.test", "t1").ShouldBeTrue();
        limiter.TryAcquire("a@x.test", "t1").ShouldBeTrue();
        limiter.TryAcquire("a@x.test", "t1").ShouldBeTrue();
        limiter.TryAcquire("a@x.test", "t1").ShouldBeFalse(); // the 4th within the window is over the limit
    }

    [Fact]
    public void The_window_slides_so_a_partition_recovers()
    {
        var clock = new AdvanceableClock(DateTimeOffset.UnixEpoch);
        var limiter = LimiterFor(1, 60, clock);

        limiter.TryAcquire("a@x.test", "t1").ShouldBeTrue();
        limiter.TryAcquire("a@x.test", "t1").ShouldBeFalse(); // at the limit
        clock.Advance(TimeSpan.FromSeconds(60));              // the first admit ages out of the trailing window
        limiter.TryAcquire("a@x.test", "t1").ShouldBeTrue();  // recovered
    }

    [Fact]
    public void A_rejected_attempt_does_not_extend_the_window()
    {
        var clock = new AdvanceableClock(DateTimeOffset.UnixEpoch);
        var limiter = LimiterFor(1, 60, clock);

        limiter.TryAcquire("a@x.test", "t1").ShouldBeTrue();  // admitted at t=0
        clock.Advance(TimeSpan.FromSeconds(30));
        limiter.TryAcquire("a@x.test", "t1").ShouldBeFalse(); // rejected at t=30 — must NOT be recorded
        clock.Advance(TimeSpan.FromSeconds(30));              // t=60: only the t=0 admit ages out
        limiter.TryAcquire("a@x.test", "t1").ShouldBeTrue();  // recovered — proof the t=30 reject didn't push the window
    }

    [Fact]
    public void Partitions_are_isolated_per_email_and_tenant()
    {
        var limiter = LimiterFor(1, 60, new AdvanceableClock(DateTimeOffset.UnixEpoch));

        limiter.TryAcquire("a@x.test", "t1").ShouldBeTrue();
        limiter.TryAcquire("a@x.test", "t1").ShouldBeFalse(); // a's t1 partition is full
        limiter.TryAcquire("b@x.test", "t1").ShouldBeTrue();  // a different actor, same tenant → its own partition
        limiter.TryAcquire("a@x.test", "t2").ShouldBeTrue();  // the same actor, a different tenant → its own partition
    }
}
