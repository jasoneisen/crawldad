namespace Crawldad.Web.Infrastructure.Security;

/// <summary>Connect-time credential resolution for a backend adapter, always tenant-scoped. Resolves a
/// <c>credentialRef</c> to its live secret against the tenant's registered browsers first, then the tenant-namespaced
/// config fallback (<c>Secrets:{tenant}:{ref}</c>). There is no process-global lookup: a tenant reaches only its own refs.</summary>
public interface IConnectCredentialResolver
{
    /// <summary>Resolves <paramref name="credentialRef"/> to its secret for <paramref name="tenant"/> (the authenticated
    /// principal's, never payload data). A cross-tenant or absent ref misses identically — no existence oracle.</summary>
    /// <exception cref="SecretNotFoundException">When neither the tenant's registrations nor its config hold the ref.</exception>
    Task<string> ResolveConnectAsync(string credentialRef, string tenant, CancellationToken ct);
}
