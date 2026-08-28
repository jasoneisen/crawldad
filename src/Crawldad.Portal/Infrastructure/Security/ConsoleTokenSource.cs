using Azure.Core;

namespace Crawldad.Portal.Infrastructure.Security;

/// <summary>Acquires the portal's first-party bearer token for the console credential (issue #119 PR4). An abstraction so
/// the token source is a config-gated singleton in production (the platform managed identity) and a fake in dev/CI — the
/// portal never holds a static secret.</summary>
internal interface IConsoleTokenSource
{
    /// <summary>Acquires (and caches/refreshes, per the underlying credential) an access token for the API audience.</summary>
    /// <param name="cancellationToken">Cancels the acquisition.</param>
    /// <returns>The bearer token string.</returns>
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken);
}

/// <summary>The production <see cref="IConsoleTokenSource"/>: acquires the portal managed identity's access token for
/// <c>{audience}/.default</c> via an Azure <see cref="TokenCredential"/> (the platform <c>ManagedIdentityCredential</c>).
/// The credential caches and refreshes tokens internally, so this is a thin per-request adapter. No secret is held — the
/// token is minted by the platform against the portal's own identity.</summary>
internal sealed class ManagedIdentityConsoleTokenSource : IConsoleTokenSource
{
    private readonly TokenCredential _credential;
    private readonly string[] _scopes;

    /// <summary>Creates a token source over <paramref name="credential"/> for the API <paramref name="audience"/>.</summary>
    /// <param name="credential">The Azure token credential (the portal's managed identity in production; a fake in tests).</param>
    /// <param name="audience">The API App ID URI; the requested scope is <c>{audience}/.default</c>.</param>
    public ManagedIdentityConsoleTokenSource(TokenCredential credential, string audience)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        _credential = credential;
        _scopes = [$"{audience}/.default"];
    }

    /// <inheritdoc />
    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken).ConfigureAwait(false);
        return token.Token;
    }
}
