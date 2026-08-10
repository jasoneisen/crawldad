using System.Diagnostics;
using Crawldad.Web.Infrastructure.Browser.Real;

namespace Crawldad.Tests.Unit;

/// <summary>The global request throttle: disabled at <c>minIntervalMs ≤ 0</c>, and otherwise spacing serialized callers at
/// least the interval apart. Timing is asserted as a lower bound (<see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// never returns early), keeping the test deterministic and fast.</summary>
public class ThrottleGateTests
{
    [Fact]
    public async Task Zero_interval_disables_throttling()
    {
        using var gate = new ThrottleGate(TimeProvider.System);
        var sw = Stopwatch.StartNew();
        await gate.WaitAsync(0, CancellationToken.None);
        await gate.WaitAsync(-5, CancellationToken.None);
        sw.Stop();
        sw.ElapsedMilliseconds.ShouldBeLessThan(100);
    }

    [Fact]
    public async Task Spaces_the_second_request_by_at_least_the_interval()
    {
        const int Interval = 200;
        using var gate = new ThrottleGate(TimeProvider.System);

        var sw = Stopwatch.StartNew();
        await gate.WaitAsync(Interval, CancellationToken.None); // first — no prior tick, proceeds immediately
        await gate.WaitAsync(Interval, CancellationToken.None); // second — waits out the interval
        sw.Stop();

        sw.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(Interval - 20); // Task.Delay never returns early
    }
}
