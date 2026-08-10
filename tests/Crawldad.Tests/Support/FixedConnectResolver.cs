using Crawldad.Web.Infrastructure.Security;

namespace Crawldad.Tests.Support;

/// <summary>An <see cref="IConnectCredentialResolver"/> that resolves every ref to one fixed secret — enough to drive
/// the remote-adapter connect paths (a token, a connect URL, or an API key) without a store or vault. Ignores the
/// tenant, so a directly-constructed backend test needn't supply one.</summary>
/// <param name="secret">The value returned for any reference.</param>
internal sealed class FixedConnectResolver(string secret) : IConnectCredentialResolver
{
    public Task<string> ResolveConnectAsync(string credentialRef, string tenant, CancellationToken ct) => Task.FromResult(secret);
}
