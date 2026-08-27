using Alba;
using Crawldad.Tests.Support;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Crawldad.Tests.Integration;

/// <summary>An Alba host for the billing slice. Booted in <b>Development</b> so the deterministic in-process
/// <c>FakeBillingGateway</c> is selected (the production Stripe stub is covered by unit tests + the Production-host boot),
/// with a fake webhook secret the fake verifier accepts and an extra env tenant that carries a tier (to exercise the
/// config endpoint's env-tier path). The primary tenant's key is layered on every scenario; the anonymous webhook and
/// per-scenario tenants override the header themselves.</summary>
public sealed class BillingApiFixture : IAsyncLifetime
{
    /// <summary>The webhook signature the fake gateway accepts (bound as Billing:Stripe:WebhookSecret).</summary>
    public const string WebhookSecret = "whsec_billing_test_0123456789";

    /// <summary>An env tenant configured with a tier, for the config endpoint's env-tier branch.</summary>
    public const string TieredTenantId = "tenant-tiered";

    /// <summary>The tiered env tenant's key.</summary>
    public const string TieredTenantKey = "tiered-key-0123456789abcdef";

    /// <summary>The tiered env tenant's configured tier.</summary>
    public const string TieredTenantTier = "scale";

    public IAlbaHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Host = (await AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Development"); // Development → FakeBillingGateway is selected
            builder.UseCrawldadTestDefaults("crawldad_billing");
            builder.UseSetting("Billing:Stripe:WebhookSecret", WebhookSecret);

            // A third env tenant carrying a tier — GET /billing/config resolves its current tier from the env descriptor.
            builder.UseSetting("Crawldad:Tenants:2:Id", TieredTenantId);
            builder.UseSetting("Crawldad:Tenants:2:ApiKey", TieredTenantKey);
            builder.UseSetting("Crawldad:Tenants:2:Actor", "tiered@crawldad.test");
            builder.UseSetting("Crawldad:Tenants:2:Tier", TieredTenantTier);

            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(new FakeClock()));
        })).AuthenticatedAsPrimaryTenant();

        await Host.ResetAllMartenDataAsync();
    }

    public Task DisposeAsync() => Host.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class BillingApiCollection : ICollectionFixture<BillingApiFixture>
{
    public const string Name = "billing-api";
}
