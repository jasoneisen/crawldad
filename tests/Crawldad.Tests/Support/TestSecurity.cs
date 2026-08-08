using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Crawldad.Web.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace Crawldad.Tests.Support;

/// <summary>An <see cref="ISecretStoreRegistry"/> with a single keyed vault adapter — the CD-6 analogue of Runner's
/// <c>SingleBackendRegistry</c>, so an interpreter unit test can drive a <c>fill.secret</c> against one in-memory vault.</summary>
/// <param name="vault">The vault kind (e.g. <see cref="SecretVaults.Config"/>).</param>
/// <param name="store">The vault adapter registered under that kind.</param>
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

/// <summary>
/// An <see cref="IRunSecretScope"/> whose <see cref="Current"/> is a fixed set, so a unit test can drive the
/// <see cref="CredentialScrubber"/>'s exact-match rule without opening an ambient scope. <see cref="Begin"/> and
/// <see cref="Register"/> are inert (unit tests supply the secrets up front).
/// </summary>
/// <param name="secrets">The secrets the scrubber sees as live for the run.</param>
internal sealed class StubSecretScope(params string[] secrets) : IRunSecretScope
{
    public IReadOnlyCollection<string> Current { get; } = secrets;

    public IDisposable Begin() => new NoopScope();

    public void Register(string secret)
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
/// credential mode (a browserless token, a browserbase apiKey). Both resolution surfaces read the same map (the form-fill
/// path is tenant-scoped in production, but a test map keys by reference); the leak suite's form-fill variant exercises the
/// <b>real</b> <see cref="ConfigurationSecretStore"/> tenant scoping via configuration instead.</summary>
/// <param name="secrets">Reference → secret map.</param>
internal sealed class MapSecretStore(IReadOnlyDictionary<string, string> secrets) : ISecretStore
{
    public Task<string> ResolveAsync(string credentialRef, CancellationToken ct) =>
        secrets.TryGetValue(credentialRef, out var secret) ? Task.FromResult(secret) : throw new SecretNotFoundException(credentialRef);

    public Task<string> ResolveForTenantAsync(string reference, string tenant, CancellationToken ct) =>
        secrets.TryGetValue(reference, out var secret) ? Task.FromResult(secret) : throw new SecretNotFoundException(reference);
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that records every rendered log line. Wrapped by the host's scrubbing logger factory
/// like any provider, so the lines it captures are exactly what a real sink would write (post-scrub) — the leak suite
/// asserts no credential reaches it.
/// </summary>
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
