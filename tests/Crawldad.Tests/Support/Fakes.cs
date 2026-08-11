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
