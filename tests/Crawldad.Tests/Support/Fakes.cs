namespace Crawldad.Tests.Support;

/// <summary>
/// Pins "now" so event-metadata timestamps are deterministic once the aggregates land. A minimal
/// <see cref="TimeProvider"/> double; if a test ever needs to advance time, reach for the BCL's
/// <c>FakeTimeProvider</c> (Microsoft.Extensions.Time.Testing) — not worth the extra package for a frozen clock.
/// </summary>
public sealed class FakeClock : TimeProvider
{
    public static readonly DateTimeOffset Fixed = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Fixed;
}
