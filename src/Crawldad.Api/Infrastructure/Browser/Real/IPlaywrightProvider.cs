using System.Diagnostics.CodeAnalysis;
using Microsoft.Playwright;

namespace Crawldad.Api.Infrastructure.Browser.Real;

/// <summary>Owns the one shared <see cref="IPlaywright"/> driver for the process, created once and lazily shared by
/// every real adapter. Browsers/contexts opened on it stay per-run and isolated. Registered as a DI singleton,
/// disposed with the host.</summary>
internal interface IPlaywrightProvider
{
    /// <summary>Returns the shared driver, creating it on first use.</summary>
    /// <param name="ct">Cancels the (first-use) driver creation.</param>
    ValueTask<IPlaywright> GetAsync(CancellationToken ct);
}

/// <summary>The lazy, thread-safe <see cref="IPlaywrightProvider"/>: a double-checked <see cref="Playwright.CreateAsync"/>
/// behind a gate, disposed with the host.</summary>
internal sealed class PlaywrightProvider : IPlaywrightProvider, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;

    [SuppressMessage("Reliability", "CA1508:Avoid dead conditional code",
        Justification = "Double-checked locking: another caller can set _playwright between the lock-free fast path and acquiring the gate, so the ??= re-check is not dead — the dataflow analyzer cannot model the concurrency.")]
    public async ValueTask<IPlaywright> GetAsync(CancellationToken ct)
    {
        if (_playwright is not null)
        {
            return _playwright;
        }

        await _gate.WaitAsync(ct);
        try
        {
            return _playwright ??= await Playwright.CreateAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _playwright?.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
