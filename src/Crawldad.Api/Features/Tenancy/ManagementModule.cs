using Crawldad.Api.Infrastructure.Security;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Api.Features.Tenancy;

/// <summary>Self-registration for the tenancy slice: the single-tenanted registry documents (queried before any tenant
/// scope exists) and the interim management endpoints. The DB-backed auth resolution itself is wired host-wide in
/// <see cref="Crawldad.Api.HostConfiguration"/>'s tenant-security boundary — the management surface is only its write side,
/// mapped solely when a management key is configured.</summary>
public static class ManagementModule
{
    /// <summary>Registers the registry documents single-tenanted (they define tenants, so they cannot be tenant-scoped),
    /// with indexes on the auth-lookup hash and the owning tenant id.</summary>
    public static void ConfigureMarten(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Schema.For<RegistryTenant>().SingleTenanted();

        var keys = options.Schema.For<TenantApiKey>();
        keys.SingleTenanted();
        keys.Index(x => x.KeyHash);   // the auth hot-path lookup
        keys.Index(x => x.TenantId);  // list/revoke by tenant

        // The console membership documents (issue #119 PR4): single-tenanted like the registry (they define who may become
        // a tenant scope, so they cannot themselves be tenant-scoped), indexed for the two lookups — the console hot path
        // "which tenant for this (email, workspace)" and "which workspaces for this user".
        var memberships = options.Schema.For<TenantMembership>();
        memberships.SingleTenanted();
        memberships.Index(x => x.Email);     // console lookup + a user's workspaces
        memberships.Index(x => x.TenantId);  // a tenant's members + the last-owner invariant
        // Active-membership uniqueness (issue #119 PR6, PR#154 forward item): a partial UNIQUE index on (TenantId, Email)
        // over ACTIVE rows only (RevokedAt is null) is the DB-level backstop to the store's advisory-lock guard — a revoked
        // row and a re-created active row for the same pair coexist, but two active rows cannot. The store serialises creates
        // under the tenant lock so this never fires in practice; it is defence-in-depth against any future non-store writer.
        memberships.Index(
            x => new { x.TenantId, x.Email },
            x =>
            {
                x.IsUnique = true;
                x.Predicate = "(data ->> 'RevokedAt') is null";
            });
        // Optimistic concurrency (issue #119 PR5, PR#153's forward-looking guard): a stale write to a membership loses, so a
        // future member-management endpoint (PR6) is safe before it exists. The cross-row last-Owner count is covered by the
        // tenant advisory lock in the store; the version covers a same-document race.
        memberships.UseOptimisticConcurrency(true);

        // The console-write audit documents (issue #119 PR5): single-tenanted (they record console authority decisions that
        // resolve before any tenant scope), indexed by tenant for a tenant's console-activity view.
        var audit = options.Schema.For<ConsoleAuditEntry>();
        audit.SingleTenanted();
        audit.Index(x => x.TenantId);
    }

    /// <summary>Maps the management endpoints under <c>/management</c>, guarded by the constant-time management-key filter
    /// — but only when a management key is configured. With no key the group is never mapped, so every
    /// <c>/management/…</c> request is a plain <c>404</c> (the documented "disabled" behaviour).</summary>
    public static void MapManagementEndpoints(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Services.GetRequiredService<IOptions<ManagementOptions>>().Value.Enabled)
        {
            return; // management disabled → routes unmapped → /management/* is a 404
        }

        // AllowAnonymous keeps the tenant authorization layer off this group (it has its own key filter); the filter runs
        // before every handler and 401s any request without the configured management key.
        var group = app.MapGroup("/management").AllowAnonymous().AddEndpointFilter<ManagementKeyFilter>();

        group.MapPost("/tenants", ManagementEndpoints.CreateTenant);
        group.MapGet("/tenants/{id}", ManagementEndpoints.GetTenant);
        group.MapPost("/tenants/{id}/suspend", ManagementEndpoints.SuspendTenant);
        group.MapPost("/tenants/{id}/reactivate", ManagementEndpoints.ReactivateTenant);
        group.MapPost("/tenants/{id}/keys", ManagementEndpoints.IssueKey);
        group.MapGet("/tenants/{id}/keys", ManagementEndpoints.ListKeys);
        group.MapDelete("/tenants/{id}/keys/{keyId}", ManagementEndpoints.RevokeKey);
    }
}
