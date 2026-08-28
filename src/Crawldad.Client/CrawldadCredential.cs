using Crawldad.Contracts.Tenancy;

namespace Crawldad.Client;

/// <summary>How a <see cref="CrawldadClient"/> authenticates each request — a <c>TokenCredential</c>-shaped seam applied
/// per request (async, so a token can be refreshed and per-request headers stamped, and so the client stays
/// concurrency-safe with no shared default-header mutation). The API key case is <see cref="ApiKeyCredential"/> (the
/// default, back-compatible with every existing caller); the portal's first-party console case is
/// <see cref="ConsoleCredential"/>; tests supply a <see cref="DelegateCredential"/>.</summary>
public interface ICrawldadCredential
{
    /// <summary>Stamps the credential's authentication onto <paramref name="request"/> just before it is sent. Called once
    /// per request; must not mutate shared state.</summary>
    /// <param name="request">The outbound request to authenticate.</param>
    /// <param name="cancellationToken">Cancels any token acquisition.</param>
    ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

/// <summary>The default credential: sends the per-tenant API key as <c>Authorization: Bearer &lt;key&gt;</c> — the API's
/// primary convention. Constructing a <see cref="CrawldadClient"/> with a <see cref="CrawldadClientOptions.ApiKey"/> and no
/// explicit credential wraps the key in one of these, so every existing caller is unchanged.</summary>
public sealed class ApiKeyCredential : ICrawldadCredential
{
    private readonly string _apiKey;

    /// <summary>Creates a credential for <paramref name="apiKey"/>.</summary>
    /// <param name="apiKey">The per-tenant API key. Required, non-blank.</param>
    /// <exception cref="ArgumentException"><paramref name="apiKey"/> is null, empty, or whitespace.</exception>
    public ApiKeyCredential(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey;
    }

    /// <inheritdoc />
    public ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        return ValueTask.CompletedTask;
    }
}

/// <summary>The portal's first-party console credential (issue #119): sends a bearer token from an injected token source
/// plus the two selector headers naming the acting user and workspace. The token proves the caller is the portal; the
/// selectors name an <i>already-granted</i> <c>(email, workspace)</c> membership the API resolves as the authority. The
/// token source is a delegate so this SDK stays free of any cloud-identity dependency — the portal plugs in its managed
/// identity, and tests plug in a stub.</summary>
public sealed class ConsoleCredential : ICrawldadCredential
{
    private readonly Func<CancellationToken, ValueTask<string>> _tokenSource;
    private readonly string _consoleUser;
    private readonly string _workspace;

    /// <summary>Creates a console credential.</summary>
    /// <param name="tokenSource">Acquires the portal's first-party bearer token (refresh-aware; called per request).</param>
    /// <param name="consoleUser">The verified acting user (normalized email) — the <c>X-Crawldad-Console-User</c> selector.</param>
    /// <param name="workspace">The active workspace (tenant id) — the <c>X-Crawldad-Workspace</c> selector.</param>
    /// <exception cref="ArgumentNullException"><paramref name="tokenSource"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="consoleUser"/> or <paramref name="workspace"/> is blank.</exception>
    public ConsoleCredential(Func<CancellationToken, ValueTask<string>> tokenSource, string consoleUser, string workspace)
    {
        ArgumentNullException.ThrowIfNull(tokenSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(consoleUser);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        _tokenSource = tokenSource;
        _consoleUser = consoleUser;
        _workspace = workspace;
    }

    /// <inheritdoc />
    public async ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await _tokenSource(cancellationToken).ConfigureAwait(false);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation(ConsoleAuthHeaders.ConsoleUser, _consoleUser);
        request.Headers.TryAddWithoutValidation(ConsoleAuthHeaders.Workspace, _workspace);
    }
}

/// <summary>A credential that defers entirely to a supplied delegate — the test/CI seam for exercising any authentication
/// shape (a test-issued console token, a plain key) without a real identity provider.</summary>
public sealed class DelegateCredential : ICrawldadCredential
{
    private readonly Func<HttpRequestMessage, CancellationToken, ValueTask> _apply;

    /// <summary>Creates a credential that applies <paramref name="apply"/> to each request.</summary>
    /// <param name="apply">Stamps whatever authentication the request needs.</param>
    /// <exception cref="ArgumentNullException"><paramref name="apply"/> is null.</exception>
    public DelegateCredential(Func<HttpRequestMessage, CancellationToken, ValueTask> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        _apply = apply;
    }

    /// <inheritdoc />
    public ValueTask ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _apply(request, cancellationToken);
}
