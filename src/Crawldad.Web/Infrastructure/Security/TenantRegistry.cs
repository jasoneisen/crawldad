using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Crawldad.Web.Infrastructure.Security;

/// <summary>
/// The configured tenant set (CD-1, §12): Crawldad is hosted-only and multi-tenant, and the tenant is the billing subject,
/// so tenancy identity is load-bearing. This is the first-slice tenant directory — a config-bound list of tenants (id, api
/// key, actor identity) under <c>Crawldad:Tenants</c>. It is the seam later tickets hang per-tenant bindings off
/// (CD-2 storage, CD-6 vault); there is deliberately no tenant-management endpoint here.
/// </summary>
public sealed class TenantOptions
{
    /// <summary>The configuration section this binds from (its <see cref="Tenants"/> reads <c>Crawldad:Tenants</c>).</summary>
    public const string Section = "Crawldad";

    /// <summary>The configured tenants (bound from <c>Crawldad:Tenants</c>).</summary>
    public IList<TenantDescriptor> Tenants { get; init; } = [];
}

/// <summary>One configured tenant. The <see cref="ApiKey"/> is a secret — it authenticates the tenant and is never echoed
/// in a response, event, or log (it is wired into the credential scrubber as an always-on redaction, see
/// <see cref="CredentialScrubber"/>).</summary>
public sealed class TenantDescriptor
{
    /// <summary>The stable tenant id — the Marten tenant partition key and the billing subject.</summary>
    public string Id { get; init; } = "";

    /// <summary>The tenant's presented API key (a secret; hash-compared, never stored in the registry as plaintext).</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>The actor/display identity stamped on this tenant's mutation events (§12) — never taken from a request body.</summary>
    public string Actor { get; init; } = "";

    /// <summary>An optional per-tenant override of the global concurrent-run cap (CD-3, docs/PRODUCT.md §Pv.3): the
    /// per-tenant slot allowance a pricing tier sets. Null defers to the global
    /// <see cref="Crawldad.Web.Features.Runs.RunLimitsOptions.MaxConcurrentRunsPerTenant"/>. This is the CD-1 tenant-config
    /// seam later pricing tiers hang off, so the admission cap is per-tenant now that tenancy identity exists.</summary>
    public int? MaxConcurrentRuns { get; init; }

    /// <summary>An optional per-tenant override of the global admission-queue depth (CD-16, docs/PRODUCT.md §Pv.3): the
    /// per-tier at-cap wait room (Free 10 / Team 100 / Scale 1,000) before a further at-cap run is rejected
    /// <c>429 queue_depth_exceeded</c>. Null defers to the global
    /// <see cref="Crawldad.Web.Features.Runs.RunLimitsOptions.MaxQueueDepthPerTenant"/> — the same override pattern as
    /// <see cref="MaxConcurrentRuns"/>.</summary>
    public int? MaxQueueDepth { get; init; }
}

/// <summary>The identity an authenticated request resolves to: the tenant partition id and the actor stamped on its
/// mutation events. Carries no secret.</summary>
/// <param name="Id">The tenant id (Marten partition key).</param>
/// <param name="Actor">The actor identity (event <c>by</c>).</param>
public readonly record struct AuthenticatedTenant(string Id, string Actor);

/// <summary>
/// Validates a presented API key against the configured tenants (CD-1). Keys are <b>hash-compared</b>: the registry keeps
/// only a SHA-256 of each configured key (never the plaintext) and compares with a fixed-time equality so a bad key leaks
/// no timing signal about which — or whether a prefix — matched. The raw keys are treated as secrets elsewhere (the
/// scrubber redacts them); here only their hashes live. Also the authority on the configured tenant ids for the
/// out-of-request tenant fan-out (the executor's startup recovery scan, which must resume every tenant's interrupted runs).
/// </summary>
public sealed class TenantRegistry
{
    private readonly IReadOnlyList<Entry> _entries;

    /// <summary>Builds the registry from the bound options, validating each tenant. A misconfiguration (missing id/actor,
    /// too-short or duplicate key) throws at construction so the host fails loudly at boot rather than silently admitting
    /// or rejecting requests.</summary>
    /// <param name="options">The bound tenant options.</param>
    /// <exception cref="InvalidOperationException">When a tenant is malformed or two tenants collide on id or key.</exception>
    public TenantRegistry(IOptions<TenantOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var entries = new List<Entry>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tenant in options.Value.Tenants)
        {
            if (string.IsNullOrWhiteSpace(tenant.Id))
            {
                throw new InvalidOperationException("a configured tenant has no id");
            }

            // A tenant id namespaces its per-tenant secret-vault keys (CD-6, Secrets:{tenant}:{ref}); a ':' in the id would
            // make that prefix ambiguous (tenant "a" + ref "b:c" vs tenant "a:b" + ref "c" resolve the same key). Reject at boot.
            if (tenant.Id.Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"tenant id '{tenant.Id}' must not contain ':' (it namespaces per-tenant secret-vault keys, CD-6)");
            }

            if (string.IsNullOrWhiteSpace(tenant.Actor))
            {
                throw new InvalidOperationException($"tenant '{tenant.Id}' has no actor identity");
            }

            if (tenant.ApiKey.Length < MinApiKeyLength)
            {
                throw new InvalidOperationException($"tenant '{tenant.Id}' has an api key shorter than {MinApiKeyLength} characters");
            }

            if (!seenIds.Add(tenant.Id))
            {
                throw new InvalidOperationException($"tenant id '{tenant.Id}' is configured more than once");
            }

            if (tenant.MaxConcurrentRuns is < 1)
            {
                throw new InvalidOperationException($"tenant '{tenant.Id}' has a maxConcurrentRuns override below 1");
            }

            if (tenant.MaxQueueDepth is < 1)
            {
                throw new InvalidOperationException($"tenant '{tenant.Id}' has a maxQueueDepth override below 1");
            }

            var hash = Hash(tenant.ApiKey);
            if (entries.Any(e => CryptographicOperations.FixedTimeEquals(e.KeyHash, hash)))
            {
                throw new InvalidOperationException($"tenant '{tenant.Id}' reuses another tenant's api key");
            }

            entries.Add(new Entry(new AuthenticatedTenant(tenant.Id, tenant.Actor), hash, tenant.MaxConcurrentRuns, tenant.MaxQueueDepth));
        }

        _entries = entries;
    }

    /// <summary>The minimum configured API-key length (a weak/short key is a boot-time misconfiguration).</summary>
    public const int MinApiKeyLength = 16;

    /// <summary>Every configured tenant id — the fan-out set for the out-of-request recovery scan (each tenant's
    /// interrupted runs must be found and resumed under that tenant, §11/CD-1).</summary>
    public IReadOnlyCollection<string> TenantIds => [.. _entries.Select(e => e.Tenant.Id)];

    /// <summary>Resolves a presented API key to its tenant, or fails. Fixed-time over the whole set: every entry is probed
    /// (no early return) so neither the match position nor a near-miss is observable through timing.</summary>
    /// <param name="presentedKey">The key from the request (<c>Authorization: Bearer</c> or <c>X-Api-Key</c>).</param>
    /// <param name="tenant">The resolved tenant when the key is valid.</param>
    /// <returns><see langword="true"/> when the key matches a configured tenant.</returns>
    public bool TryAuthenticate(string presentedKey, [NotNullWhen(true)] out AuthenticatedTenant? tenant)
    {
        ArgumentNullException.ThrowIfNull(presentedKey);
        var presentedHash = Hash(presentedKey);
        AuthenticatedTenant? match = null;
        foreach (var entry in _entries)
        {
            if (CryptographicOperations.FixedTimeEquals(presentedHash, entry.KeyHash))
            {
                match = entry.Tenant;
            }
        }

        tenant = match;
        return match is not null;
    }

    /// <summary>The tenant's configured concurrent-run override (CD-3), or null when it defers to the global default. Looked
    /// up by the admission gate to resolve the tenant's slot cap. An unknown tenant id yields null (defers to the global).</summary>
    /// <param name="tenantId">The tenant id to look up.</param>
    /// <param name="limit">The configured override when present.</param>
    /// <returns><see langword="true"/> when the tenant configured an override.</returns>
    public bool TryGetConcurrencyOverride(string tenantId, out int limit)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Tenant.Id, tenantId, StringComparison.Ordinal) && entry.MaxConcurrentRuns is { } configured)
            {
                limit = configured;
                return true;
            }
        }

        limit = 0;
        return false;
    }

    /// <summary>The tenant's configured admission-queue-depth override (CD-16), or null when it defers to the global default.
    /// Looked up by the run queue to resolve the tenant's max queue depth. An unknown tenant id yields null (defers to the
    /// global) — the same override shape as <see cref="TryGetConcurrencyOverride"/>.</summary>
    /// <param name="tenantId">The tenant id to look up.</param>
    /// <param name="depth">The configured override when present.</param>
    /// <returns><see langword="true"/> when the tenant configured an override.</returns>
    public bool TryGetQueueDepthOverride(string tenantId, out int depth)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        foreach (var entry in _entries)
        {
            if (string.Equals(entry.Tenant.Id, tenantId, StringComparison.Ordinal) && entry.MaxQueueDepth is { } configured)
            {
                depth = configured;
                return true;
            }
        }

        depth = 0;
        return false;
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private sealed record Entry(AuthenticatedTenant Tenant, byte[] KeyHash, int? MaxConcurrentRuns, int? MaxQueueDepth);
}
