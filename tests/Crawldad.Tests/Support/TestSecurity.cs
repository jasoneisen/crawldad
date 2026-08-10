using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace Crawldad.Tests.Support;

/// <summary>An <see cref="ISecretStoreRegistry"/> with a single keyed vault adapter — the analogue of Runner's
/// <c>SingleBackendRegistry</c>, so an interpreter unit test can drive a <c>fill.secret</c> against one in-memory vault.</summary>
/// <param name="vault">The vault kind (e.g. <see cref="SecretVaults.Config"/>).</param>
internal sealed class SingleSecretVaultRegistry(string vault, ISecretStore store) : ISecretStoreRegistry
{
    public bool TryResolve(string requested, [NotNullWhen(true)] out ISecretStore? resolved)
    {
        if (string.Equals(requested, vault, StringComparison.Ordinal))
        {
            resolved = store;
            return true;
        }

        resolved = null;
        return false;
    }
}

/// <summary>An <see cref="IRunSecretScope"/> whose <see cref="Current"/> is a fixed set, so a unit test can drive the
/// <see cref="CredentialScrubber"/>'s exact-match rule without an ambient scope. <see cref="Begin"/>/<see cref="Register"/> are inert.</summary>
/// <param name="secrets">The secrets the scrubber sees as live for the run.</param>
internal sealed class StubSecretScope(params string[] secrets) : IRunSecretScope
{
    public IReadOnlyCollection<string> Current { get; } = secrets;

    /// <summary>The form-fill secrets the scrubber sees at the lower form floor; defaults to none.</summary>
    public IReadOnlyCollection<string> FormSecrets { get; init; } = [];

    public IDisposable Begin() => new NoopScope();

    public void Register(string secret)
    {
        // Inert: the fixed set is supplied at construction.
    }

    public void RegisterFormSecret(string secret)
    {
        // Inert: the fixed set is supplied at construction.
    }

    private sealed class NoopScope : IDisposable
    {
        public void Dispose()
        {
            // Nothing to release.
        }
    }
}

/// <summary>An <see cref="ISecretStore"/> mapping references to secrets — the leak suite maps a distinct sentinel per
/// credential mode. The leak suite's form-fill variant instead exercises the <b>real</b>
/// <see cref="ConfigurationSecretStore"/> tenant scoping via configuration.</summary>
internal sealed class MapSecretStore(IReadOnlyDictionary<string, string> secrets) : ISecretStore
{
    public Task<string> ResolveForTenantAsync(string reference, string tenant, CancellationToken ct) =>
        secrets.TryGetValue(reference, out var secret) ? Task.FromResult(secret) : throw new SecretNotFoundException(reference);
}

/// <summary>An <see cref="ISecretStore"/> returning one fixed secret and <b>counting</b> tenant-scoped resolutions — so a
/// test can prove a <c>fill.secret</c> re-resolves from the vault on a checkpoint resume (rather than restoring a persisted
/// value).</summary>
internal sealed class CountingVault(string secret) : ISecretStore
{
    /// <summary>How many times the form-fill (tenant-scoped) resolution has been called.</summary>
    public int Calls { get; private set; }

    /// <summary>Resets the call count (between the fresh run and the resumed run).</summary>
    public void Reset() => Calls = 0;

    public Task<string> ResolveForTenantAsync(string reference, string tenant, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(secret);
    }
}

/// <summary>An <see cref="ILoggerProvider"/> that records every rendered log line. Wrapped by the host's scrubbing
/// logger factory like any provider, so captured lines are exactly what a real sink would write (post-scrub) — the
/// leak suite asserts no credential reaches it.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>A snapshot of the captured lines.</summary>
    public IReadOnlyList<string> Lines => [.. _lines];

    /// <summary>Drops all captured lines (called before each leak run so its assertions see only that run's output).</summary>
    public void Clear() => _lines.Clear();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _lines);

    public void Dispose()
    {
        // No unmanaged state.
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            sink.Enqueue($"[{category}] {formatter(state, exception)}" + (exception is null ? string.Empty : " EX:" + exception));
    }
}
