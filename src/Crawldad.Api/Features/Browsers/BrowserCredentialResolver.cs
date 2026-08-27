using Crawldad.Api.Infrastructure.Security;

namespace Crawldad.Api.Features.Browsers;

/// <summary>The tenant-scoped <see cref="IConnectCredentialResolver"/>: resolves a connect <c>credentialRef</c> against
/// the tenant's registered browsers first, then the tenant-namespaced config fallback (<c>Secrets:{tenant}:{ref}</c>).
/// A registered credential wins; a total miss surfaces the config store's <see cref="SecretNotFoundException"/>.</summary>
internal sealed class BrowserCredentialResolver(IBrowserCredentialStore store, ISecretStore config) : IConnectCredentialResolver
{
    public async Task<string> ResolveConnectAsync(string credentialRef, string tenant, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(credentialRef);
        ArgumentException.ThrowIfNullOrEmpty(tenant);
        var registered = await store.TryResolveSecretAsync(tenant, credentialRef, ct);
        return registered ?? await config.ResolveForTenantAsync(credentialRef, tenant, ct);
    }
}
