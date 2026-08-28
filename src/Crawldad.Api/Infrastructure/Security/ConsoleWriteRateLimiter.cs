using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>A small in-memory sliding-window rate limiter for console writes (issue #119 PR5), keyed per
/// <c>(email, tenant)</c>. Each partition keeps a log of recent write timestamps; a write is admitted only if fewer than
/// <see cref="ConsoleWriteOptions.PermitLimit"/> fall within the trailing <see cref="ConsoleWriteOptions.Window"/>, so the
/// limit slides continuously (no fixed-window burst-at-the-boundary). Time comes from the injected <see cref="TimeProvider"/>,
/// so tests drive it deterministically. This is <b>abuse insurance</b>: generous defaults, per-instance (a scaled-out fleet
/// multiplies the ceiling — acceptable, since the point is to bound a single compromised console, not to meter traffic). It
/// keys on the human email + tenant — never the shared portal identity — so one noisy actor can't starve other tenants.</summary>
public sealed class ConsoleWriteRateLimiter
{
    private readonly TimeProvider _clock;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    /// <summary>Builds the limiter from the bound <see cref="ConsoleWriteOptions"/> and the host clock.</summary>
    public ConsoleWriteRateLimiter(IOptions<ConsoleWriteOptions> options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clock = clock;
        _permitLimit = options.Value.PermitLimit;
        _window = options.Value.Window;
    }

    /// <summary>Records an attempt for <paramref name="email"/> acting on <paramref name="tenantId"/> and returns whether it
    /// is admitted. A rejected attempt is <b>not</b> recorded (a caller over the limit does not push its own window forward),
    /// so the partition recovers exactly one window after its most recent admitted write.</summary>
    public bool TryAcquire(string email, string tenantId)
    {
        // A newline cannot appear in a normalized email or a GUID tenant id, so the partition key is unambiguous.
        var bucket = _buckets.GetOrAdd(tenantId + "\n" + email, static _ => new Bucket());
        var now = _clock.GetUtcNow();
        var cutoff = now - _window;
        lock (bucket.Gate)
        {
            bucket.Prune(cutoff);
            if (bucket.Timestamps.Count >= _permitLimit)
            {
                return false; // over the sliding limit — reject, and do not extend the window
            }

            bucket.Timestamps.Enqueue(now);
            return true;
        }
    }

    // A single partition's timestamp log, pruned to the trailing window on each access under its own gate.
    private sealed class Bucket
    {
        public object Gate { get; } = new();

        public Queue<DateTimeOffset> Timestamps { get; } = new();

        public void Prune(DateTimeOffset cutoff)
        {
            while (Timestamps.TryPeek(out var timestamp) && timestamp <= cutoff)
            {
                Timestamps.Dequeue();
            }
        }
    }
}
