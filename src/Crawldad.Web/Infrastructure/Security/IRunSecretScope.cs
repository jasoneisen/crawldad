using System.Collections.Concurrent;

namespace Crawldad.Web.Infrastructure.Security;

/// <summary>
/// The per-run secret registry (§12, WP3): the exact-string half of the scrubbing boundary. Credentials are resolved
/// by reference only at connect time (<see cref="ISecretStore"/>), and the resolving adapter <see cref="Register"/>s the
/// live secret here so every sink's <see cref="CredentialScrubber"/> can catch it by <b>exact match</b> — even in
/// free-form text that no query-param rule would recognise (a payload <c>log</c> message interpolating an input, an
/// exception message, a scraped page that echoes the value).
/// <para>
/// A scope is <b>per run</b>: the run's entry point opens one with <see cref="Begin"/> and disposes it when the run ends,
/// which clears the registered secrets. Nothing here is process-global mutable state — the current scope is ambient
/// (flows down the run's async call chain via <see cref="AsyncLocal{T}"/>), so concurrent runs never see one another's
/// secrets and <b>no secret outlives its run</b> in memory-resident sink state (the scrubber holds only a reference to
/// this seam, never a secret).
/// </para>
/// </summary>
public interface IRunSecretScope
{
    /// <summary>
    /// Opens the ambient secret scope for the current run. The returned handle must be disposed when the run ends
    /// (a <c>using</c> around the whole run); disposal clears every registered secret and detaches the ambient scope.
    /// </summary>
    /// <returns>The scope handle; dispose it to clear the run's secrets.</returns>
    IDisposable Begin();

    /// <summary>
    /// Records a live secret resolved for the current run so the scrubber redacts it by exact match. A no-op when no
    /// scope is open on the current async context (e.g. an adapter connect outside a run) — the secret is simply not
    /// registered, never stored globally.
    /// </summary>
    /// <param name="secret">The resolved secret (an account token, an API key, or a whole connect URL, §9.1).</param>
    void Register(string secret);

    /// <summary>The secrets registered for the current run's scope, or an empty set when no scope is open.</summary>
    IReadOnlyCollection<string> Current { get; }
}

/// <summary>
/// The ambient <see cref="IRunSecretScope"/>: the current run's secrets live in an <see cref="AsyncLocal{T}"/> bag that
/// flows down the run's async call chain and is discarded when the scope is disposed. Registered as a singleton — it
/// holds no secret state of its own (only the <see cref="AsyncLocal{T}"/> slot), so a resolved credential exists in
/// memory only inside the bag of the run that resolved it, and only until that run disposes its scope.
/// </summary>
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

    public IReadOnlyCollection<string> Current => _ambient.Value?.Snapshot() ?? [];

    // One run's secret set. A ConcurrentDictionary (used as a set) makes Register on the connect path and Snapshot on a
    // background log thread safe without a lock. Disposal clears the set AND detaches the ambient slot, so nothing
    // survives the run.
    private sealed class RunSecretBag : IDisposable
    {
        private readonly ConcurrentDictionary<string, byte> _secrets = new(StringComparer.Ordinal);

        public void Add(string secret) => _secrets.TryAdd(secret, 0);

        public IReadOnlyCollection<string> Snapshot() => _secrets.IsEmpty ? [] : [.. _secrets.Keys];

        public void Dispose()
        {
            _secrets.Clear();
            _ambient.Value = null;
        }
    }
}
