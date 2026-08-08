using Crawldad.Web.Infrastructure.Security;

namespace Crawldad.Tests.Support;

/// <summary>An <see cref="ISecretStore"/> that resolves every reference to one fixed secret — enough to drive the
/// remote-adapter connect paths (a token, a connect URL, or an API key) without a real vault.</summary>
/// <param name="secret">The value returned for any reference.</param>
internal sealed class FixedSecretStore(string secret) : ISecretStore
{
    public Task<string> ResolveAsync(string credentialRef, CancellationToken ct) => Task.FromResult(secret);

    public Task<string> ResolveForTenantAsync(string reference, string tenant, CancellationToken ct) => Task.FromResult(secret);
}
