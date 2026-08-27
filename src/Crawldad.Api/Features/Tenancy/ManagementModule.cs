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
