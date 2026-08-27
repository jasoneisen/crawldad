using Crawldad.Portal.Tenancy;
using Crawldad.Tests.Client;

namespace Crawldad.Tests.Portal;

/// <summary>Test doubles for rendering the payloads/webhooks data pages under bUnit: a programmable
/// <see cref="IPortalTenantContext"/> that resolves to a <see cref="PortalTenant"/> whose <c>CrawldadClient</c> rides a
/// stub HTTP handler (canned API responses, keyed by path/method), or to the not-linked state (null). Named for this
/// feature so it never collides with a sibling page task's own doubles.</summary>
internal sealed class PayloadsWebhooksTenantContext : IPortalTenantContext
{
    private readonly PortalTenant? _tenant;

    private PayloadsWebhooksTenantContext(PortalTenant? tenant) => _tenant = tenant;

    /// <summary>A context that resolves to a tenant whose client is backed by <paramref name="handler"/>.</summary>
    public static PayloadsWebhooksTenantContext LinkedTo(StubHttpMessageHandler handler, string tenantId = "meridian-title") =>
        new(new PortalTenant(tenantId, ClientTestHarness.ClientFor(handler)));

    /// <summary>A context that resolves to the not-linked state (unauthenticated or unlinked).</summary>
    public static PayloadsWebhooksTenantContext NotLinked() => new(tenant: null);

    public Task<PortalTenant?> TryResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(_tenant);

    public Task<PortalTenant> RequireAsync(CancellationToken cancellationToken = default) =>
        _tenant is not null
            ? Task.FromResult(_tenant)
            : throw new NotLinkedException("The current portal user is not linked to a Crawldad tenant.");
}
