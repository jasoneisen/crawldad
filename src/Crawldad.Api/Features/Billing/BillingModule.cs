using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Crawldad.Api.Features.Billing;

/// <summary>Self-registration for the Billing slice: the single-tenanted processed-event dedup document, the bound tier
/// catalog + provider options, and the payment-provider gateway seam — the deterministic <see cref="FakeBillingGateway"/>
/// in Development (and tests), the fail-closed <see cref="StripeBillingGateway"/> stub everywhere else. The endpoints
/// (checkout/portal/config + the anonymous webhook) are Wolverine.Http, discovered by attribute; nothing to map here.
/// Mirrors the Webhooks/Browsers module shape, and the environment-conditional wiring mirrors the portal's email sender.</summary>
public static class BillingModule
{
    /// <summary>Registers the anti-replay document single-tenanted (the registry partition — the webhook is
    /// unauthenticated and not tenant-scoped, so like the registry documents it lives on the default partition).</summary>
    public static void ConfigureMarten(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Schema.For<ProcessedBillingEvent>().SingleTenanted();
    }

    /// <summary>Registers the slice's services: the bound options, the resolved tier catalog, the Marten dedup store, and
    /// the provider gateway chosen by environment. The Stripe gateway is a fail-closed stub (no Stripe NuGet dependency
    /// yet), so a production boot without credentials degrades to friendly "not yet available", never a crash.</summary>
    public static void AddBillingServices(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<BillingOptions>().BindConfiguration(BillingOptions.Section);
        builder.Services.AddSingleton<BillingCatalog>();
        builder.Services.AddSingleton<IProcessedBillingEventStore, MartenProcessedBillingEventStore>();

        if (builder.Environment.IsDevelopment())
        {
            builder.Services.AddSingleton<IBillingGateway, FakeBillingGateway>();
        }
        else
        {
            builder.Services.AddSingleton<IBillingGateway, StripeBillingGateway>();
        }
    }
}
