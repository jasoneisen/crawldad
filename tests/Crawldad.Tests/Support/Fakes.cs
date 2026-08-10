namespace Crawldad.Tests.Support;

/// <summary>Pins "now" for deterministic event-metadata timestamps. A minimal <see cref="TimeProvider"/> double — for
/// tests that need to advance time, use the BCL's <c>FakeTimeProvider</c> (Microsoft.Extensions.Time.Testing) instead.</summary>
public sealed class FakeClock : TimeProvider
{
    public static readonly DateTimeOffset Fixed = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Fixed;
}
