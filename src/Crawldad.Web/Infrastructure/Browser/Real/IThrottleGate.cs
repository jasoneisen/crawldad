namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>
/// The global request throttle (§8.1): every non-cached request passes through one serialized gate that lets at most
/// one request per <c>minIntervalMs</c> tick proceed, reproducing <c>PlaywrightFactory</c>'s <c>_reqSemaphore</c> +
/// <c>PeriodicTimer</c> (its global 2 s throttle). A DI singleton so the throttle is process-wide across runs and
/// regions, keeping load on a shared target site bounded.
/// </summary>
internal interface IThrottleGate
{
    /// <summary>Blocks until this request may proceed: it serializes callers and spaces them at least
    /// <paramref name="minIntervalMs"/> apart.</summary>
    /// <param name="minIntervalMs">The minimum spacing between released requests; 0 or less disables throttling.</param>
    /// <param name="ct">Cancels the wait.</param>
    Task WaitAsync(int minIntervalMs, CancellationToken ct);
}

/// <summary>
/// The serialized <see cref="IThrottleGate"/>: a one-slot semaphore holds the gate while a released request's interval
/// elapses, so the next caller cannot proceed until the tick passes — the requests come out at least
/// <c>minIntervalMs</c> apart. Uses the injected <see cref="TimeProvider"/> for the delay (registered with the system
/// clock, since throttling is inherently wall-clock — a frozen test clock must not freeze it).
/// </summary>
/// <param name="clock">The time source for the inter-request delay.</param>
internal sealed class ThrottleGate(TimeProvider clock) : IThrottleGate, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public async Task WaitAsync(int minIntervalMs, CancellationToken ct)
    {
        if (minIntervalMs <= 0)
        {
            return; // throttling disabled — pass straight through
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
