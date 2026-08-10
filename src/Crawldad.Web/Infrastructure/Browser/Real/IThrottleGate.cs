namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>The global request throttle: every non-cached request passes through one serialized gate that lets at
/// most one request per <c>minIntervalMs</c> tick proceed. A DI singleton, so the throttle is process-wide across
/// runs and regions, keeping load on a shared target site bounded.</summary>
internal interface IThrottleGate
{
    /// <summary>Blocks until this request may proceed, serializing callers and spacing them at least
    /// <paramref name="minIntervalMs"/> apart (0 or less disables throttling).</summary>
    Task WaitAsync(int minIntervalMs, CancellationToken ct);
}

/// <summary>The serialized <see cref="IThrottleGate"/>: a one-slot semaphore holds the gate until the released
/// request's interval elapses, so requests come out at least <c>minIntervalMs</c> apart. Uses the injected
/// <see cref="TimeProvider"/> registered with the system clock — throttling is wall-clock, so a frozen test clock must not freeze it.</summary>
internal sealed class ThrottleGate(TimeProvider clock) : IThrottleGate, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public async Task WaitAsync(int minIntervalMs, CancellationToken ct)
    {
        if (minIntervalMs <= 0)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var wait = _nextAllowed - clock.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, clock, ct);
            }

            _nextAllowed = clock.GetUtcNow() + TimeSpan.FromMilliseconds(minIntervalMs);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Releases the gate's semaphore (invoked by the container at host shutdown for the singleton).</summary>
    public void Dispose() => _gate.Dispose();
}
