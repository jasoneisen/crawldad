using Crawldad.Api.Infrastructure.Security;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>Create a registry tenant. <see cref="Actor"/> defaults to the id when omitted; <see cref="Tier"/> defaults to
/// empty; <see cref="SlotAllowance"/> null defers to the global concurrent-run default.</summary>
/// <param name="Id">The tenant id slug (partition key + billing subject).</param>
/// <param name="DisplayName">The human-facing display name.</param>
/// <param name="Actor">The actor stamped on the tenant's mutation events (defaults to <paramref name="Id"/>).</param>
/// <param name="Tier">The plan tier moniker.</param>
/// <param name="SlotAllowance">The per-tenant concurrent-run override, or null for the global default.</param>
public sealed record CreateTenantRequest(string Id, string DisplayName, string? Actor = null, string? Tier = null, int? SlotAllowance = null);

/// <summary>A registry tenant, as returned by the management endpoints. Carries no secret.</summary>
/// <param name="Id">The tenant id.</param>
/// <param name="DisplayName">The display name.</param>
/// <param name="Actor">The actor stamped on mutation events.</param>
/// <param name="Status">The lifecycle state.</param>
/// <param name="Tier">The plan tier moniker.</param>
/// <param name="SlotAllowance">The per-tenant concurrent-run override, or null.</param>
/// <param name="CreatedAt">When the tenant was created.</param>
/// <param name="UpdatedAt">When the tenant was last written.</param>
public sealed record TenantResponse(
    string Id,
    string DisplayName,
    string Actor,
    TenantStatus Status,
    string Tier,
    int? SlotAllowance,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The one-time result of issuing a key: the raw <see cref="ApiKey"/> is returned <b>here and only here</b> — it
/// is never persisted (only its hash is) and can never be retrieved again. The <see cref="KeyId"/> is the revoke handle.</summary>
/// <param name="KeyId">The key record id (the revoke handle).</param>
/// <param name="Prefix">The non-secret display prefix.</param>
/// <param name="ApiKey">The full raw key — shown once; store it now.</param>
/// <param name="CreatedAt">When the key was issued.</param>
public sealed record IssueKeyResponse(Guid KeyId, string Prefix, string ApiKey, DateTimeOffset CreatedAt);

/// <summary>One key in a listing — prefixes and metadata only, never the secret or its hash.</summary>
/// <param name="KeyId">The key record id.</param>
/// <param name="Prefix">The non-secret display prefix.</param>
/// <param name="CreatedAt">When the key was issued.</param>
/// <param name="LastUsedAt">When the key was last used (best-effort), or null if never.</param>
/// <param name="RevokedAt">When the key was revoked, or null while active.</param>
/// <param name="Active">Whether the key is currently active (not revoked).</param>
public sealed record ApiKeySummary(
    Guid KeyId,
    string Prefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    bool Active);

/// <summary>A tenant's keys (prefixes only), newest first.</summary>
/// <param name="Keys">The key summaries.</param>
public sealed record KeyListResponse(IReadOnlyList<ApiKeySummary> Keys);
