using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Crawldad.Portal.Tenancy;

/// <summary>Development-only convenience: the values of the optional <c>Portal:DevTenantLink</c> config section, which
/// seeds/updates a single tenant link at startup so a developer can exercise the data pages without the (not-yet-built)
/// account UI. The <see cref="ApiKey"/> is a real tenant credential — supply it via user-secrets or an environment
/// variable (<c>Portal__DevTenantLink__ApiKey</c>), NEVER committed to appsettings.</summary>
internal sealed class DevTenantLinkOptions
{
    /// <summary>The account email to link (matched case-insensitively to the signed-in user).</summary>
    public string? Email { get; set; }

    /// <summary>The Crawldad tenant the account acts as.</summary>
    public string? TenantId { get; set; }

    /// <summary>The tenant's Crawldad API key. Protected at rest by the store like any other link — never stored plaintext.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>Seeds the <c>Portal:DevTenantLink</c> link at startup, in Development only. Registered as a hosted service
/// AFTER Marten's schema-apply-on-startup, so the "portal" tables exist by the time it writes. A missing or partial
/// section is a no-op — the portal boots cleanly with no link, exactly as production does.</summary>
internal sealed class DevTenantLinkSeeder(
    IPortalTenantLinkStore links,
    IOptions<DevTenantLinkOptions> options,
    ILogger<DevTenantLinkSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Email) || string.IsNullOrWhiteSpace(o.TenantId) || string.IsNullOrWhiteSpace(o.ApiKey))
        {
            logger.LogDebug("{Section} is not fully configured — no dev tenant link seeded.", PortalTenancy.DevTenantLinkSection);
            return;
        }

        var link = await links.UpsertAsync(o.Email, o.TenantId, o.ApiKey, cancellationToken);
        logger.LogInformation("Seeded dev tenant link for {Email} → tenant {TenantId}.", link.Email, link.TenantId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
