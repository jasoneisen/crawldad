using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crawldad.Api.Infrastructure.Storage;

/// <summary>The retention/lifecycle janitor: a host-enforced scheduled sweep that deletes durable blobs past their
/// category's TTL, per <see cref="RetentionOptions"/>. Sweeps every registered <see cref="IRetentionStore"/> (blobs) and
/// every registered <see cref="IRetentionSweep"/> (non-blob durable rows, e.g. an async run's stored result in
/// <c>RunProgress</c>) on the same cadence; under the in-memory fake provider no store is registered, so the blob leg is
/// a harmless no-op.</summary>
public sealed class RetentionJanitor : BackgroundService
{
    private readonly IRetentionStore[] _stores;
    private readonly IRetentionSweep[] _sweeps;
    private readonly RetentionOptions _retention;
    private readonly TimeProvider _clock;
    private readonly ILogger<RetentionJanitor> _logger;

    /// <summary>Wires the janitor to the durable blob stores + non-blob sweeps it drives and the retention policy it enforces.</summary>
    public RetentionJanitor(IEnumerable<IRetentionStore> stores, IEnumerable<IRetentionSweep> sweeps, IOptions<StorageOptions> options, TimeProvider clock, ILogger<RetentionJanitor> logger)
    {
        ArgumentNullException.ThrowIfNull(stores);
        ArgumentNullException.ThrowIfNull(sweeps);
        ArgumentNullException.ThrowIfNull(options);
        _stores = [.. stores];
        _sweeps = [.. sweeps];
        _retention = options.Value.Retention;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Sweeps every durable store once, deleting each blob older than its category's TTL (a TTL of ≤ 0
    /// retains that category indefinitely), then runs every non-blob <see cref="IRetentionSweep"/>. Pure with respect to
    /// <paramref name="now"/> so a test drives expiry deterministically.</summary>
    public async Task<int> SweepOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var deleted = 0;
        foreach (var store in _stores)
        {
            foreach (var blob in await store.ListAsync(ct))
            {
                if (_retention.TtlFor(blob.Kind) is not { } ttl)
                {
                    continue; // this category's retention is disabled (TTL ≤ 0) — keep indefinitely
                }

                if (now - blob.LastModifiedUtc < ttl)
                {
                    continue; // still within its retention window
                }

                if (await TryDeleteAsync(store, blob, ct))
                {
                    deleted++;
                }
            }
        }

        // The non-blob sweeps (each owns its own TTL check and per-pass bound): a run's stored result in RunProgress, etc.
        foreach (var sweep in _sweeps)
        {
            deleted += await TrySweepAsync(sweep, now, ct);
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Retention janitor expired {Count} item(s).", deleted);
        }

        return deleted;
    }

    // A single blob's delete failure (an Azure throttle/5xx, a file that vanished mid-sweep, a permission blip) must not abort
    // the rest of the sweep — log it and move on. Cancellation is not a failure: it propagates so shutdown stops promptly.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A retention sweep must tolerate any transient per-blob storage error and continue; cancellation is re-thrown by the exception filter.")]
    private async Task<bool> TryDeleteAsync(IRetentionStore store, StoredBlob blob, CancellationToken ct)
    {
        try
        {
            return await store.DeleteAsync(blob, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Retention janitor failed to delete {Kind} blob {Tenant}/{Key}; continuing.", blob.Kind, blob.Tenant, blob.Key);
            return false;
        }
    }

    // A non-blob sweep's failure (a DB blip, a Marten "global lock" wait, a transient tenant query error) must not abort
    // the rest of the pass — log it and move on, returning zero for this sweep. Cancellation propagates so shutdown stops
    // promptly, exactly like TryDeleteAsync.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A retention pass must tolerate any transient per-sweep failure and continue; cancellation is re-thrown by the exception filter.")]
    private async Task<int> TrySweepAsync(IRetentionSweep sweep, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            return await sweep.SweepAsync(now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Retention janitor sweep {Sweep} failed; continuing.", sweep.GetType().Name);
            return 0;
        }
    }

    /// <summary>The periodic driver: sweeps immediately, then every <see cref="RetentionOptions.SweepInterval"/>, until the
    /// host stops. A no-op when retention is disabled.</summary>
    /// <param name="stoppingToken">Cancelled on host shutdown.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_retention.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await SweepSafelyAsync(stoppingToken);
            await DelayAsync(_retention.SweepInterval, stoppingToken);
        }
    }

    // Runs one sweep, absorbing any failure so it never stops the host (the default BackgroundService behaviour is
    // StopHost). Cancellation (shutdown) is re-thrown by the filter so the host tears down promptly; internal so
    // both filter branches are directly testable.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A background janitor must survive any transient sweep failure rather than stop the host; cancellation is re-thrown by the exception filter.")]
    internal async Task SweepSafelyAsync(CancellationToken ct)
    {
        try
        {
            await SweepOnceAsync(_clock.GetUtcNow(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Retention sweep failed; retrying next interval.");
        }
    }

    // The inter-sweep wait, swallowing the shutdown cancellation so the loop exits cleanly via its while condition.
    private async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _clock, ct);
        }
        catch (OperationCanceledException)
        {
            // host shutdown during the wait — fall through so the while condition observes the cancellation and exits
        }
    }
}
