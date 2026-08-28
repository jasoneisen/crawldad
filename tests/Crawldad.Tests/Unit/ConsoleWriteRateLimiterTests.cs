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

    [Fact]
    public void Idle_partitions_are_evicted_by_the_sweep_while_active_ones_survive()
    {
        var clock = new AdvanceableClock(DateTimeOffset.UnixEpoch);
        var limiter = LimiterFor(5, 60, clock);

        limiter.TryAcquire("a@x.test", "t1").ShouldBeTrue(); // t=0: the sweep schedules its next run at t=60
        clock.Advance(TimeSpan.FromSeconds(30));
        limiter.TryAcquire("b@x.test", "t1").ShouldBeTrue(); // t=30: no sweep yet (before t=60); two partitions now
        limiter.PartitionCount.ShouldBe(2);

        clock.Advance(TimeSpan.FromSeconds(40));             // t=70: past the next-sweep time and a's last write is > a window old
        limiter.TryAcquire("b@x.test", "t1").ShouldBeTrue(); // triggers the sweep: a (idle since t=0) is dropped, b (t=30) is kept

        limiter.PartitionCount.ShouldBe(1);                  // exactly the still-active partition remains — the map doesn't grow unbounded
    }

    [Fact]
    public void An_evicted_partition_starts_fresh_when_seen_again()
    {
        var clock = new AdvanceableClock(DateTimeOffset.UnixEpoch);
        var limiter = LimiterFor(1, 60, clock);

        limiter.TryAcquire("a@x.test", "t1").ShouldBeTrue();
        limiter.TryAcquire("a@x.test", "t1").ShouldBeFalse();  // at the limit
        clock.Advance(TimeSpan.FromSeconds(120));              // idle a full window+ → the next call sweeps a away
        limiter.TryAcquire("a@x.test", "t1").ShouldBeTrue();   // a fresh partition, admitted (equivalent to the window sliding)
        limiter.PartitionCount.ShouldBe(1);
    }
}
