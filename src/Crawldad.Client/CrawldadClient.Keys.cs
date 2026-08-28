using Crawldad.Contracts.Tenancy;

namespace Crawldad.Client;

/// <summary>Tenant self-service API key management: list / mint / rotate / revoke the authenticated tenant's <b>own</b>
/// keys, authenticated by the tenant key itself (a tenant acting on itself — no management credential). The raw key is
/// returned <b>exactly once</b>, from mint and rotate, and the SDK never logs it. Rotating or revoking the very key this
/// client authenticates with is refused server-side — rotate returns a replacement to swap to. First-class for automation
/// / MCP / agents rotating their own keys, and the surface the portal's account area drives.</summary>
public sealed partial class CrawldadClient
{
    /// <summary>Lists the authenticated tenant's API keys (<c>GET /tenant/keys</c>) — prefixes and metadata only (never a
    /// raw key or its hash), newest first. The key authenticating this request is flagged
    /// <see cref="TenantApiKeyInfo.Current"/>.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The tenant's key listing.</returns>
    /// <exception cref="CrawldadUnauthorizedException">The API key is missing or not valid (<c>401</c>).</exception>
    /// <exception cref="CrawldadApiException">Self-service is unavailable for this env-configured tenant — its keys are operator-managed (<c>400 self_service_unavailable</c>).</exception>
    public Task<TenantApiKeyList> ListTenantKeysAsync(CancellationToken ct = default) =>
        GetAsync<TenantApiKeyList>("tenant/keys", ct);

    /// <summary>Mints a new API key for the authenticated tenant (<c>POST /tenant/keys</c>). The full raw key is on the
    /// returned <see cref="TenantApiKeyCreated.ApiKey"/> <b>once</b> — store it now; only its hash is persisted and it can
    /// never be retrieved again.</summary>
    /// <param name="label">An optional display label to tell the key apart in a listing (trimmed, at most 64 chars), or null.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The minted key, including its one-time raw value.</returns>
    /// <exception cref="CrawldadValidationException">The label failed validation (<c>400</c>).</exception>
    /// <exception cref="CrawldadApiException">Self-service is unavailable for this env-configured tenant (<c>400 self_service_unavailable</c>).</exception>
    public Task<TenantApiKeyCreated> MintTenantKeyAsync(string? label = null, CancellationToken ct = default) =>
        SendJsonAsync<TenantApiKeyCreated>(HttpMethod.Post, "tenant/keys", new CreateTenantKeyRequest(label), ct);

    /// <summary>Rotates one of the tenant's keys (<c>POST /tenant/keys/{id}/rotate</c>): mints a replacement and revokes
    /// the old key atomically, returning the replacement's one-time raw key. This is the anti-lockout way to replace the
    /// key you are currently using — swap to the returned key. The replacement inherits the rotated key's label.</summary>
    /// <param name="keyId">The id of the key to rotate.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The replacement key, including its one-time raw value.</returns>
    /// <exception cref="CrawldadNotFoundException">No such active key for this tenant (<c>404</c>).</exception>
    /// <exception cref="CrawldadApiException">Self-service is unavailable for this env-configured tenant (<c>400 self_service_unavailable</c>).</exception>
    public Task<TenantApiKeyCreated> RotateTenantKeyAsync(Guid keyId, CancellationToken ct = default) =>
        PostAsync<TenantApiKeyCreated>($"tenant/keys/{keyId}/rotate", ct);

    /// <summary>Revokes one of the tenant's keys (<c>DELETE /tenant/keys/{id}</c>); the revoke takes effect immediately.
    /// Revoking the tenant's last active key, or the key this client authenticates with, is refused (<c>409</c>) — rotate
    /// it instead (<see cref="RotateTenantKeyAsync"/>).</summary>
    /// <param name="keyId">The id of the key to revoke.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="CrawldadNotFoundException">No such active key for this tenant (<c>404</c>).</exception>
    /// <exception cref="CrawldadApiException">The key is the last active key or the current key — rotate instead (<c>409</c>); or self-service is unavailable for this tenant (<c>400</c>).</exception>
    public Task RevokeTenantKeyAsync(Guid keyId, CancellationToken ct = default) =>
        SendNoContentAsync(HttpMethod.Delete, $"tenant/keys/{keyId}", ct);
}
