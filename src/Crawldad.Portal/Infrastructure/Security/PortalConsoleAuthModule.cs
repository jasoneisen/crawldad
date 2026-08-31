using Azure.Identity;
using Crawldad.Portal.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Portal.Infrastructure.Security;

/// <summary>The portal's console-mode wiring (issue #119 PR4). Registers the console-auth options + boot guard always, and
/// — only when <c>Crawldad:ConsoleAuth</c> is fully configured — the managed-identity token source and the
/// <see cref="ConsoleClientFactory"/> that builds console-mode API clients. Absent config, neither is registered, so
/// <see cref="PortalTenantContext"/> resolves its honest "console access not configured" state for data pages. Mirrors the
/// email / Data-Protection modules' config-gating idiom (read the section directly at registration time; a half-set pair is
/// rejected by the boot validator). Dev/CI never uses the real credential — a fake token source is injected in tests.</summary>
public static class PortalConsoleAuthModule
{
    /// <summary>Registers the console-auth options + boot guard, and — only when the section is configured — the
    /// managed-identity token source and console client factory.</summary>
    /// <param name="services">The DI container.</param>
    /// <param name="configuration">The host configuration (console-mode is read from <c>Crawldad:ConsoleAuth</c>).</param>
    public static void AddConsoleAuth(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The knobs + boot guard (a half-configured pair fails at startup rather than silently staying on the key path).
        services.AddOptions<PortalConsoleAuthOptions>().BindConfiguration(PortalConsoleAuthOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<PortalConsoleAuthOptions>, PortalConsoleAuthOptionsValidator>();

        // The wiring choice is a registration-time decision, so read the section directly (IOptions isn't available yet) —
        // the same indexer idiom the Data-Protection module uses.
        var tenantId = configuration[$"{PortalConsoleAuthOptions.Section}:TenantId"];
        var audience = configuration[$"{PortalConsoleAuthOptions.Section}:Audience"];
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(audience))
        {
            return; // no config → console-mode off → data pages show "console access not configured"; a half-set pair fails at boot
        }

        // The portal's own managed identity mints the console token — no static secret. Construction is I/O-free (the token
        // is acquired only on the first console request). The container runs under a USER-ASSIGNED identity only, and a
        // parameterless ManagedIdentityCredential targets the system-assigned identity (it does not read AZURE_CLIENT_ID —
        // only DefaultAzureCredential does), so the client id must be passed explicitly or acquisition fails at runtime.
        // Tests replace this singleton with a fake token source.
        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var credential = string.IsNullOrWhiteSpace(clientId)
            ? new ManagedIdentityCredential()
            : new ManagedIdentityCredential(clientId);
        services.AddSingleton<IConsoleTokenSource>(new ManagedIdentityConsoleTokenSource(credential, audience));
        services.AddSingleton<ConsoleClientFactory>();
    }
}
