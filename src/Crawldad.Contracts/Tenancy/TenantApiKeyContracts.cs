using System.Text.Json.Serialization;

namespace Crawldad.Contracts.Tenancy;

/// <summary>The <c>POST /tenant/keys</c> request body: mint a new API key for the authenticated tenant, with an optional
/// human <see cref="Label"/> to tell keys apart in a listing (e.g. <c>ci</c>, <c>laptop</c>, <c>mcp-agent</c>). The label
/// is metadata only — it never affects authentication. An absent, empty, or whitespace label mints an unlabelled key.</summary>
/// <param name="Label">An optional display label for the key (trimmed; at most 64 characters), or null for none.</param>
public sealed record CreateTenantKeyRequest(string? Label = null);

/// <summary>One of the authenticated tenant's API keys, as returned by <c>GET /tenant/keys</c> — <b>metadata only</b>. It
/// carries the key's non-secret display <see cref="Prefix"/> and never the raw key or its hash (the raw key exists only in
/// the one-time <see cref="TenantApiKeyCreated"/> mint/rotate response).</summary>
/// <param name="KeyId">The key record id — the handle a rotate or revoke targets.</param>
/// <param name="Prefix">The non-secret display prefix (<c>ck_&lt;env&gt;_&lt;first-chars&gt;</c>).</param>
/// <param name="Label">The optional display label, or omitted when the key is unlabelled.</param>
/// <param name="CreatedAt">When the key was issued (UTC).</param>
/// <param name="LastUsedAt">When the key was last used (best-effort), or null if never.</param>
/// <param name="RevokedAt">When the key was revoked, or null while it is active.</param>
/// <param name="Active">Whether the key is currently active (not revoked).</param>
/// <param name="Current">Whether this is the key authenticating the request that read the list — the caller's "this session"
/// key. Exactly one active key is <c>current</c> per request; it is the key a rotate replaces and the one a plain revoke
/// refuses (rotate it instead).</param>
public sealed record TenantApiKeyInfo(
    Guid KeyId,
    string Prefix,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    bool Active,
    bool Current);

/// <summary>The <c>GET /tenant/keys</c> response: the authenticated tenant's keys, newest first — prefixes and metadata
/// only, never a raw key or its hash.</summary>
/// <param name="Keys">The tenant's key summaries.</param>
public sealed record TenantApiKeyList(IReadOnlyList<TenantApiKeyInfo> Keys);

/// <summary>The one-time result of minting (<c>POST /tenant/keys</c>) or rotating (<c>POST /tenant/keys/{id}/rotate</c>) a
/// key: the full raw <see cref="ApiKey"/> is returned <b>here and only here</b>. It is never persisted (only its hash is)
/// and can never be retrieved again — store it now. The <see cref="KeyId"/> is the handle a later rotate or revoke targets.</summary>
/// <param name="KeyId">The new key record id (the rotate/revoke handle).</param>
/// <param name="Prefix">The non-secret display prefix.</param>
/// <param name="Label">The key's display label, or omitted when unlabelled (a rotate carries the replaced key's label).</param>
/// <param name="ApiKey">The full raw key — <c>ck_&lt;env&gt;_&lt;random&gt;</c>. Secret; shown once, store it now.</param>
/// <param name="CreatedAt">When the key was issued (UTC).</param>
public sealed record TenantApiKeyCreated(
    Guid KeyId,
    string Prefix,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label,
    string ApiKey,
    DateTimeOffset CreatedAt);
