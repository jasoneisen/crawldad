using System.Diagnostics.CodeAnalysis;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts;
using Crawldad.Contracts.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Wolverine.Http;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>The self-serve free-tier provisioning surface (issue #119 PR7): <c>POST /provisioning/tenants</c>. It is the ONE
/// console endpoint reachable with <b>no membership</b> — a brand-new user has no workspace yet — so it is authenticated by
/// the portal's <c>ConsolePrincipal</c> scheme <b>only</b> (a key caller is a 401; see <see cref="ProvisioningEndpoints"/> +
/// <see cref="ConsoleAuthModule.ProvisioningPolicy"/>), and the acting user is read from the portal-pinned
/// <see cref="ConsoleAuthHeaders.ConsoleUser"/> selector rather than a tenant claim (there is none).
///
/// <para>It enforces <b>one free tenant per email, EVER</b>: the atomic create in <see cref="IFreeTenantProvisioningStore"/>
/// writes a lifetime marker under a per-email advisory lock, so a revoked/left membership never resets the entitlement and a
/// concurrent double-submit yields exactly one workspace. The created tenant gets an API-assigned GUID id, the free-tier
/// defaults (2 slots, tier <c>free</c>; queue depth defers to the global default — the registry carries no per-tenant queue
/// field), and the creator as Owner. No API key is minted — a console user needs none; they mint from the keys UI. Each
/// attempt is rate-limited (abuse insurance) and audited (attribution).</para></summary>
public static class ProvisioningEndpoint
{
    /// <summary>The free-tier concurrent-run slot allowance (BUSINESS_MODEL.md Free = 2), stamped as the tenant's
    /// per-tenant override so <c>GET /tenant</c>/<c>GET /usage</c> report exactly the cap the admission gate enforces.</summary>
    public const int FreeSlotAllowance = 2;

    /// <summary>The free-tier moniker stamped on a provisioned tenant.</summary>
    public const string FreeTier = "free";

    /// <summary>The workspace display name used when the request supplies none.</summary>
    public const string DefaultDisplayName = "My workspace";

    // The rate-limiter partition for provisioning attempts. Provisioning has no tenant yet, so it cannot ride the
    // per-(email, tenant) console-write partition; it keys on (email, THIS sentinel) instead. The sentinel is not a valid
    // tenant slug (it starts with '~', which TenantRules.IsValidId rejects and a t-<guid> id never contains), so it can never
    // collide with a real tenant's console-write partition for the same email.
    private const string _rateLimitPartition = "~free-provision";

    /// <summary>Handles <c>POST /provisioning/tenants</c>: provision the caller's one free workspace, or refuse a second.</summary>
    [WolverinePost(ProvisioningEndpoints.ProvisionRoute)]
    public static async Task<IResult> Provision(
        ProvisionTenantRequest? request,
        HttpContext http,
        IFreeTenantProvisioningStore provisioning,
        ConsoleWriteRateLimiter rateLimiter,
        IConsoleAuditStore audit,
        TimeProvider clock,
        ILogger<ProvisioningEndpointLog> logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);

        // The acting user: the portal-pinned console-user selector (re-normalized — the header's casing is never trusted),
        // NOT a tenant claim (provisioning runs before any membership exists). The console scheme already proved this is the
        // portal; the header names WHICH verified user it acts for, exactly as every other console call.
        var rawUser = http.Request.Headers[ConsoleAuthHeaders.ConsoleUser].ToString();
        if (string.IsNullOrWhiteSpace(rawUser))
        {
            return ProvisioningProblems.ActorRequired();
        }

        var email = EmailAddress.Normalize(rawUser);

        var displayName = request?.DisplayName;
        if (displayName is { } supplied && supplied.Length > TenantRules.MaxDisplayNameLength)
        {
            return ProvisioningProblems.InvalidDisplayName(TenantRules.MaxDisplayNameLength);
        }

        // Abuse insurance BEFORE any DB work (and before the audit, so the limiter also bounds audit volume under abuse) —
        // over the sliding limit is a 429 and nothing is created. One-per-email already caps successful creates at one; this
        // bounds a compromised console hammering the create/refuse path.
        if (!rateLimiter.TryAcquire(email, _rateLimitPartition))
        {
            return ProvisioningProblems.RateLimited();
        }

        var now = clock.GetUtcNow();
        var tenant = new RegistryTenant
        {
            Id = $"t-{Guid.NewGuid()}",                                   // API-assigned GUID id (registry convention)
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? DefaultDisplayName : displayName.Trim(),
            Actor = email,                                                // a personal free workspace: its creator is the key-path actor
            Tier = FreeTier,
            SlotAllowance = FreeSlotAllowance,
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var result = await provisioning.ProvisionAsync(email, tenant, now, ct);

        if (result.Outcome == FreeProvisionOutcome.AlreadyProvisioned)
        {
            await AuditAsync(audit, logger, result.TenantId, email, StatusCodes.Status409Conflict, now);
            return ProvisioningProblems.AlreadyProvisioned(result.TenantId);
        }

        await AuditAsync(audit, logger, result.TenantId, email, StatusCodes.Status201Created, now);
        return Results.Json(
            new WorkspaceSummary(tenant.Id, tenant.DisplayName, MembershipRole.Owner),
            statusCode: StatusCodes.Status201Created);
    }

    // Appends one console-audit row for the provision attempt (attribution: who provisioned/attempted, and the outcome).
    // Best-effort like the console-write audit: the tenant is already committed, so a store fault must never turn a successful
    // create into a 500 — it is logged and swallowed. Uses an uncancellable token so a client that disconnected after
    // triggering the create is still audited. Provisioning is not a ConsoleWriteEndpoint (it has no tenant claim for the
    // middleware to read), so it audits itself here rather than through the console-write middleware.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The create already committed; a console-audit append is telemetry that must never fail the request. Any store fault is logged and the request's own result stands.")]
    private static async Task AuditAsync(IConsoleAuditStore audit, ILogger logger, string tenantId, string email, int statusCode, DateTimeOffset now)
    {
        try
        {
            await audit.RecordAsync(
                new ConsoleAuditEntry
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Email = email,
                    Operation = "POST",
                    Route = ProvisioningEndpoints.ProvisionRoute,
                    StatusCode = statusCode,
                    At = now,
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "provisioning audit append failed for {TenantId}", tenantId);
        }
    }
}

/// <summary>Log-category marker for <see cref="ProvisioningEndpoint"/> (a static endpoint class cannot be an
/// <see cref="ILogger{T}"/> category), mirroring the billing endpoint's marker.</summary>
public sealed class ProvisioningEndpointLog;
