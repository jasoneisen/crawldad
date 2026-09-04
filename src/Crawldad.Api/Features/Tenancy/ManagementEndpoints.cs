using Crawldad.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>The interim, server-side tenant + key administration surface (consumed by the future portal). Every handler
/// runs behind the <see cref="ManagementKeyFilter"/>, so it needs no tenant principal; a status or key mutation
/// invalidates the auth cache in-process so it takes effect immediately. Deliberately not part of the customer OpenAPI
/// envelope — it is a separate operator surface with its own auth model (see docs/API.md, THREAT_MODEL.md).</summary>
internal static class ManagementEndpoints
{
    /// <summary><c>POST /management/tenants</c>: create a tenant. 400 on a field guard, 409 on a duplicate id. A missing
    /// body is the framework's own 400 (the body is a required parameter), so the handler only guards the fields.</summary>
    public static async Task<IResult> CreateTenant(CreateTenantRequest request, ITenantRegistryStore store, TimeProvider clock, CancellationToken ct)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return ManagementProblems.InvalidTenant(errors);
        }

        var now = clock.GetUtcNow();
        var tenant = new RegistryTenant
        {
            Id = request.Id,
            DisplayName = request.DisplayName,
            // Derived from the id, never from the request body: this value is issued as the auth principal's actor claim
            // and stamped into mutation events as `by`, so a caller-supplied one would be forged attribution.
            Actor = request.Id,
            Tier = request.Tier ?? "",
            SlotAllowance = request.SlotAllowance,
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

        return await store.CreateAsync(tenant, ct)
            ? Results.Created($"/management/tenants/{tenant.Id}", ToResponse(tenant))
            : ManagementProblems.TenantExists(request.Id);
    }

    /// <summary><c>GET /management/tenants/{id}</c>: the tenant, or 404.</summary>
    public static async Task<IResult> GetTenant(string id, ITenantRegistryStore store, CancellationToken ct)
    {
        var tenant = await store.FindAsync(id, ct);
        return tenant is null ? ManagementProblems.TenantNotFound(id) : Results.Ok(ToResponse(tenant));
    }

    /// <summary><c>POST /management/tenants/{id}/suspend</c>: suspend the tenant (its keys stop authenticating). 404 if unknown.</summary>
    public static Task<IResult> SuspendTenant(string id, ITenantRegistryStore store, TenantDirectory directory, TimeProvider clock, CancellationToken ct) =>
        SetStatusAsync(id, TenantStatus.Suspended, store, directory, clock, ct);

    /// <summary><c>POST /management/tenants/{id}/reactivate</c>: return a suspended tenant to service. 404 if unknown.</summary>
    public static Task<IResult> ReactivateTenant(string id, ITenantRegistryStore store, TenantDirectory directory, TimeProvider clock, CancellationToken ct) =>
        SetStatusAsync(id, TenantStatus.Active, store, directory, clock, ct);

    /// <summary><c>POST /management/tenants/{id}/keys</c>: issue a new key. The raw key is in the 201 body and nowhere
    /// else. 404 if the tenant is unknown.</summary>
    public static async Task<IResult> IssueKey(string id, ITenantRegistryStore store, TimeProvider clock, IOptions<TenantRegistryOptions> options, CancellationToken ct)
    {
        if (await store.FindAsync(id, ct) is null)
        {
            return ManagementProblems.TenantNotFound(id);
        }

        var minted = ApiKeyMint.Issue(options.Value.KeyEnvironmentLabel);
        var now = clock.GetUtcNow();
        var record = new TenantApiKey
        {
            Id = Guid.NewGuid(),
            TenantId = id,
            KeyHash = minted.Hash,
            Prefix = minted.Prefix,
            CreatedAt = now,
        };
        await store.AddKeyAsync(record, ct);
        return Results.Created($"/management/tenants/{id}/keys/{record.Id}", new IssueKeyResponse(record.Id, record.Prefix, minted.Raw, now));
    }

    /// <summary><c>GET /management/tenants/{id}/keys</c>: the tenant's keys (prefixes only). 404 if the tenant is unknown.</summary>
    public static async Task<IResult> ListKeys(string id, ITenantRegistryStore store, CancellationToken ct)
    {
        if (await store.FindAsync(id, ct) is null)
        {
            return ManagementProblems.TenantNotFound(id);
        }

        var keys = await store.ListKeysAsync(id, ct);
        return Results.Ok(new KeyListResponse([.. keys.Select(ToSummary)]));
    }

    /// <summary><c>DELETE /management/tenants/{id}/keys/{keyId}</c>: revoke a key. 204 on success, 404 if no such active
    /// key belongs to the tenant.</summary>
    public static async Task<IResult> RevokeKey(string id, Guid keyId, ITenantRegistryStore store, TenantDirectory directory, TimeProvider clock, CancellationToken ct)
    {
        if (!await store.RevokeKeyAsync(id, keyId, clock.GetUtcNow(), ct))
        {
            return ManagementProblems.KeyNotFound();
        }

        directory.InvalidateTenant(id); // drop cached auth for this tenant's keys so the revoke is honoured immediately
        return Results.NoContent();
    }

    private static async Task<IResult> SetStatusAsync(string id, TenantStatus status, ITenantRegistryStore store, TenantDirectory directory, TimeProvider clock, CancellationToken ct)
    {
        var tenant = await store.SetStatusAsync(id, status, clock.GetUtcNow(), ct);
        if (tenant is null)
        {
            return ManagementProblems.TenantNotFound(id);
        }

        directory.InvalidateTenant(id); // a suspend must stop the tenant's cached keys authenticating immediately
        return Results.Ok(ToResponse(tenant));
    }

    private static Dictionary<string, string[]> Validate(CreateTenantRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!TenantRules.IsValidId(request.Id))
        {
            errors["id"] = ["id must be a lowercase slug of letters, digits, and hyphens (1-64 chars, no leading/trailing hyphen)"];
        }

        if (!TenantRules.IsValidDisplayName(request.DisplayName))
        {
            errors["displayName"] = [$"displayName is required and at most {TenantRules.MaxDisplayNameLength} characters"];
        }

        if (!TenantRules.IsValidTier(request.Tier))
        {
            errors["tier"] = [$"tier is at most {TenantRules.MaxTierLength} characters"];
        }

        if (!TenantRules.IsValidSlotAllowance(request.SlotAllowance))
        {
            errors["slotAllowance"] = ["slotAllowance must be at least 1 when set"];
        }

        return errors;
    }

    private static TenantResponse ToResponse(RegistryTenant tenant) =>
        new(tenant.Id, tenant.DisplayName, tenant.Actor, tenant.Status, tenant.Tier, tenant.SlotAllowance, tenant.CreatedAt, tenant.UpdatedAt);

    private static ApiKeySummary ToSummary(TenantApiKey key) =>
        new(key.Id, key.Prefix, key.CreatedAt, key.LastUsedAt, key.RevokedAt, key.RevokedAt is null);
}
