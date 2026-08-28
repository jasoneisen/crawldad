using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wolverine.Http;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>Tenant self-service API key management — <c>/tenant/keys</c>, authenticated by the tenant's <b>own</b> key
/// (the normal ApiKey scheme; a tenant acting on itself, no management credential). List / mint / rotate / revoke, all
/// scoped strictly to the authenticated tenant: the tenant id comes only from the principal (<see cref="TenantContext"/>),
/// never a route or body, and a key id is honoured only when it belongs to the caller — a foreign or unknown id is a plain
/// <c>404</c> with no existence oracle. Reuses the operator key-mint machinery (<see cref="ApiKeyMint"/>, the registry
/// store, the auth-cache invalidation) so this rides exactly the authority the tenant key already grants.
///
/// <para><b>Registry tenants only.</b> An env-configured tenant's keys are operator config, and a registry-minted key for
/// it would never authenticate (the dead-key trap), so the whole surface is a clear <c>400</c>
/// (<see cref="TenantKeyProblems.SelfServiceUnavailable"/>) for an env tenant. The raw key is returned <b>exactly once</b>
/// (mint / rotate) and never listed, logged, or placed in a problem body. Every mutation invalidates the tenant's auth
/// cache so a revoke or rotate-out is honoured immediately on the serving instance (and within the TTL fleet-wide).</para></summary>
public static class TenantKeyEndpoints
{
    /// <summary>Handles <c>GET /tenant/keys</c>: the caller's keys, newest first — prefixes and metadata only (never a raw
    /// key or its hash), with the key authenticating this request flagged <c>current</c>.</summary>
    [WolverineGet("/tenant/keys")]
    public static async Task<IResult> List(
        TenantContext tenant,
        ITenantRegistryStore store,
        HttpContext http,
        CancellationToken ct)
    {
        if (await store.FindAsync(tenant.TenantId, ct) is null)
        {
            return TenantKeyProblems.SelfServiceUnavailable();
        }

        var currentHash = ApiKeyMint.Hash(PresentedApiKey.Read(http.Request));
        var keys = await store.ListKeysAsync(tenant.TenantId, ct);
        return Results.Ok(new TenantApiKeyList([.. keys.Select(key => ToInfo(key, currentHash))]));
    }

    /// <summary>Handles <c>POST /tenant/keys</c>: mint a new key for the caller's tenant, with an optional label. The raw
    /// key is in the <c>201</c> body and nowhere else.</summary>
    [WolverinePost("/tenant/keys")]
    public static async Task<IResult> Mint(
        CreateTenantKeyRequest request,
        [FromServices] TenantContext tenant,
        ITenantRegistryStore store,
        [FromServices] TenantDirectory directory,
        IOptions<TenantRegistryOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await store.FindAsync(tenant.TenantId, ct) is null)
        {
            return TenantKeyProblems.SelfServiceUnavailable();
        }

        var (label, labelError) = TenantKeyRules.NormalizeLabel(request.Label);
        if (labelError is not null)
        {
            return TenantKeyProblems.InvalidLabel(labelError);
        }

        var minted = ApiKeyMint.Issue(options.Value.KeyEnvironmentLabel);
        var now = clock.GetUtcNow();
        var record = new TenantApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            KeyHash = minted.Hash,
            Prefix = minted.Prefix,
            Label = label,
            CreatedAt = now,
        };
        await store.AddKeyAsync(record, ct);
        directory.InvalidateTenant(tenant.TenantId); // uniform policy: any key mutation converges this tenant's auth cache
        return Results.Created($"/tenant/keys/{record.Id}", new TenantApiKeyCreated(record.Id, record.Prefix, record.Label, minted.Raw, now));
    }

    /// <summary>Handles <c>POST /tenant/keys/{id}/rotate</c>: mint a replacement and revoke <c>{id}</c> in one transaction.
    /// The replacement's raw key is in the <c>201</c> body once and inherits the rotated key's label. Allowed even for the
    /// <c>current</c> key (that is the point of rotation) and even for the last key (a replacement is minted first, so
    /// there is no lockout). <c>404</c> when <c>{id}</c> is not one of the caller's active keys.</summary>
    [WolverinePost("/tenant/keys/{id}/rotate")]
    public static async Task<IResult> Rotate(
        Guid id,
        [FromServices] TenantContext tenant,
        [FromServices] ITenantRegistryStore store,
        [FromServices] TenantDirectory directory,
        IOptions<TenantRegistryOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await store.FindAsync(tenant.TenantId, ct) is null)
        {
            return TenantKeyProblems.SelfServiceUnavailable();
        }

        var minted = ApiKeyMint.Issue(options.Value.KeyEnvironmentLabel);
        var now = clock.GetUtcNow();
        var replacement = new TenantApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            KeyHash = minted.Hash,
            Prefix = minted.Prefix,
            CreatedAt = now,
        };

        var stored = await store.RotateKeyAsync(tenant.TenantId, id, replacement, now, ct);
        if (stored is null)
        {
            return TenantKeyProblems.KeyNotFound(); // unknown / foreign / already revoked — the minted key was never persisted
        }

        directory.InvalidateTenant(tenant.TenantId); // the rotated-out key must stop authenticating immediately
        return Results.Created($"/tenant/keys/{stored.Id}", new TenantApiKeyCreated(stored.Id, stored.Prefix, stored.Label, minted.Raw, now));
    }

    /// <summary>Handles <c>DELETE /tenant/keys/{id}</c>: revoke one of the caller's keys. Refuses to revoke the last active
    /// key or the key authenticating this request — rotate those instead (both <c>409</c>). <c>404</c> when <c>{id}</c> is
    /// not one of the caller's active keys (unknown / foreign / already revoked).</summary>
    [WolverineDelete("/tenant/keys/{id}")]
    public static async Task<IResult> Revoke(
        Guid id,
        [FromServices] TenantContext tenant,
        [FromServices] ITenantRegistryStore store,
        [FromServices] TenantDirectory directory,
        HttpContext http,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (await store.FindAsync(tenant.TenantId, ct) is null)
        {
            return TenantKeyProblems.SelfServiceUnavailable();
        }

        var keys = await store.ListKeysAsync(tenant.TenantId, ct);
        var target = keys.FirstOrDefault(key => key.Id == id);
        if (target is null || target.RevokedAt is not null)
        {
            return TenantKeyProblems.KeyNotFound(); // unknown, another tenant's (absent from this tenant's list), or already revoked
        }

        if (keys.Count(key => key.RevokedAt is null) <= 1)
        {
            return TenantKeyProblems.LastActiveKey(); // anti-lockout — the tenant's only live key
        }

        if (string.Equals(target.KeyHash, ApiKeyMint.Hash(PresentedApiKey.Read(http.Request)), StringComparison.Ordinal))
        {
            return TenantKeyProblems.CurrentKey(); // revoking the in-flight key would break this very session
        }

        await store.RevokeKeyAsync(tenant.TenantId, id, clock.GetUtcNow(), ct);
        directory.InvalidateTenant(tenant.TenantId); // drop cached auth so the revoke takes effect immediately
        return Results.NoContent();
    }

    // Projects a stored key to its secret-free listing row, flagging the one whose hash matches the request's presented
    // key as `current` (the "this session" key a rotate replaces and a plain revoke refuses).
    private static TenantApiKeyInfo ToInfo(TenantApiKey key, string currentHash) =>
        new(
            key.Id,
            key.Prefix,
            key.Label,
            key.CreatedAt,
            key.LastUsedAt,
            key.RevokedAt,
            key.RevokedAt is null,
            string.Equals(key.KeyHash, currentHash, StringComparison.Ordinal));
}
