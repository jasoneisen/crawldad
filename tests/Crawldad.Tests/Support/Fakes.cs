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
