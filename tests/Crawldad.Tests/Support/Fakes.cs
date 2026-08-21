namespace Crawldad.Tests.Support;

/// <summary>Pins "now" for deterministic event-metadata timestamps. A minimal <see cref="TimeProvider"/> double — for
/// tests that need to advance time, use the BCL's <c>FakeTimeProvider</c> (Microsoft.Extensions.Time.Testing) instead.</summary>
public sealed class FakeClock : TimeProvider
{
    public static readonly DateTimeOffset Fixed = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Fixed;
}

/// <summary>A <see cref="TimeProvider"/> whose "now" is settable, so a test can advance time between two writes (e.g. to
/// prove a registration's createdAt is preserved while updatedAt moves forward).</summary>
/// <param name="start">The initial "now".</param>
public sealed class MutableClock(DateTimeOffset start) : TimeProvider
{
    /// <summary>The current "now"; assign to advance time.</summary>
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A frozen <see cref="TimeProvider"/> that RECORDS every <see cref="System.Threading.Tasks.Task"/>-delay wait
/// asked of it — capturing the exact backoff schedule a run drives through the injected clock — and completes each one
/// immediately, so a delay-driven run neither hangs on the frozen clock nor waits real time. <see cref="Delays"/> is the
/// ordered list of requested waits (milliseconds). The same injected-clock seam the retry suite already uses, just observable.</summary>
public sealed class RecordingDelayClock : TimeProvider
{
    private readonly List<int> _delays = [];

    /// <summary>The waits requested through the clock, in order — the observed backoff sequence in milliseconds.</summary>
    public IReadOnlyList<int> Delays => _delays;

    public override DateTimeOffset GetUtcNow() => FakeClock.Fixed;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _delays.Add((int)dueTime.TotalMilliseconds);
        return base.CreateTimer(callback, state, TimeSpan.Zero, period); // fire ASAP so the recorded wait never actually elapses
    }
}

/// <summary>A <b>thread-safe</b> advanceable <see cref="TimeProvider"/>: "now" is stored as interlocked ticks so one
/// thread may <see cref="Advance"/> it while another reads it concurrently — the case a live server holds (e.g. the SSE
/// tail's heartbeat check runs on the request thread while the test advances the clock). <see cref="MutableClock"/>'s
/// plain property setter is unsafe under that concurrency.</summary>
/// <param name="start">The initial "now".</param>
public sealed class AdvanceableClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    /// <summary>Moves "now" forward by <paramref name="by"/> (atomically).</summary>
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}
