using System.Collections.Concurrent;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The per-run secret registry: the exact-string half of the scrubbing boundary. The resolving adapter
/// <see cref="Register"/>s a live secret here so every sink's <see cref="CredentialScrubber"/> can catch it by exact
/// match. Scoped per run via <see cref="AsyncLocal{T}"/>; disposing the scope clears it, so no secret outlives its run.</summary>
public interface IRunSecretScope
{
    /// <summary>Opens the ambient secret scope for the current run. The returned handle must be disposed when the run
    /// ends (a <c>using</c> around the whole run); disposal clears every registered secret and detaches the ambient
    /// scope.</summary>
    IDisposable Begin();

    /// <summary>Records a live connect credential for the current run so the scrubber redacts it by exact match; a
    /// no-op when no scope is open (never stored globally).</summary>
    /// <param name="secret">The resolved secret (an account token, an API key, or a whole connect URL).</param>
    void Register(string secret);

    /// <summary>Records a live form-fill secret for the current run. Distinct from <see cref="Register"/> because a
    /// form credential is user-chosen and may be short (a PIN, a password), so the scrubber redacts it at a much lower
    /// length floor. A no-op when no scope is open.</summary>
    void RegisterFormSecret(string secret);

    /// <summary>The <b>connect</b> secrets registered for the current run's scope, or an empty set when no scope is open.</summary>
    IReadOnlyCollection<string> Current { get; }

    /// <summary>The <b>form-fill</b> secrets (<c>fill.secret</c>) registered for the current run, redacted at the lower form floor.</summary>
    IReadOnlyCollection<string> FormSecrets { get; }
}

/// <summary>The ambient <see cref="IRunSecretScope"/>: the current run's secrets live in an <see cref="AsyncLocal{T}"/>
/// bag that flows down the async call chain and is discarded when the scope is disposed. A singleton holding no
/// secret state of its own — a resolved credential exists only inside the run that resolved it.</summary>
internal sealed class AmbientRunSecretScope : IRunSecretScope
{
    private static readonly AsyncLocal<RunSecretBag?> _ambient = new();

    public IDisposable Begin()
    {
        var bag = new RunSecretBag();
        _ambient.Value = bag;
        return bag;
    }

    public void Register(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        _ambient.Value?.Add(secret);
    }

    public void RegisterFormSecret(string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        _ambient.Value?.AddForm(secret);
    }

    public IReadOnlyCollection<string> Current => _ambient.Value?.Snapshot() ?? [];

    public IReadOnlyCollection<string> FormSecrets => _ambient.Value?.SnapshotForm() ?? [];

    // One run's secret set. A ConcurrentDictionary (used as a set) makes Register on the connect path and Snapshot on a
    // background log thread safe without a lock. Connect and form-fill secrets are kept apart for the separate length
    // floors; disposal clears both sets and detaches the ambient slot, so nothing survives the run.
    private sealed class RunSecretBag : IDisposable
    {
        private readonly ConcurrentDictionary<string, byte> _secrets = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _formSecrets = new(StringComparer.Ordinal);

        public void Add(string secret) => _secrets.TryAdd(secret, 0);

        public void AddForm(string secret) => _formSecrets.TryAdd(secret, 0);

        public IReadOnlyCollection<string> Snapshot() => _secrets.IsEmpty ? [] : [.. _secrets.Keys];

        public IReadOnlyCollection<string> SnapshotForm() => _formSecrets.IsEmpty ? [] : [.. _formSecrets.Keys];

        public void Dispose()
        {
            _secrets.Clear();
            _formSecrets.Clear();
            _ambient.Value = null;
        }
    }
}
