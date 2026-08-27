using Crawldad.Api.Features.Runs;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Unit;

/// <summary>The SSE idle-keepalive clock: a comment is due only after a full interval of silence, firing resets the
/// window (so keepalives pace at the interval, not once-then-every-poll), and a real frame resets it too — the tail's
/// "reset on traffic" rule. Purely time-driven off the injected clock, so it is deterministic here and inert under a
/// frozen host clock.</summary>
public class SseHeartbeatTests
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(15);

    [Fact]
    public void Not_due_until_a_full_idle_interval_has_elapsed()
    {
        var clock = new AdvanceableClock(FakeClock.Fixed);
        var heartbeat = new SseHeartbeat(clock, _interval);

        heartbeat.IsDue().ShouldBeFalse();                         // no time has passed
        clock.Advance(_interval - TimeSpan.FromSeconds(1));
        heartbeat.IsDue().ShouldBeFalse();                         // still inside the window
        clock.Advance(TimeSpan.FromSeconds(1));
        heartbeat.IsDue().ShouldBeTrue();                          // the window elapsed → a keepalive is due
    }

    [Fact]
    public void Firing_resets_the_window_so_keepalives_pace_at_the_interval()
    {
        var clock = new AdvanceableClock(FakeClock.Fixed);
        var heartbeat = new SseHeartbeat(clock, _interval);
        clock.Advance(_interval);
        heartbeat.IsDue().ShouldBeTrue();

        heartbeat.IsDue().ShouldBeFalse();                         // just fired — not due again until another full interval
        clock.Advance(_interval);
        heartbeat.IsDue().ShouldBeTrue();
    }

    [Fact]
    public void A_real_frame_resets_the_idle_window()
    {
        var clock = new AdvanceableClock(FakeClock.Fixed);
        var heartbeat = new SseHeartbeat(clock, _interval);
        clock.Advance(_interval - TimeSpan.FromSeconds(1));        // almost due…

        heartbeat.MarkWritten();                                   // …but a real frame flowed, resetting the timer
        clock.Advance(TimeSpan.FromSeconds(1));                    // the original window would have elapsed here
        heartbeat.IsDue().ShouldBeFalse();                         // the write pushed the next keepalive out
        clock.Advance(_interval - TimeSpan.FromSeconds(1));
        heartbeat.IsDue().ShouldBeTrue();                          // due a full interval after the write
    }
}
