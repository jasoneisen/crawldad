using Crawldad.Contracts.Webhooks;
using FluentValidation;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Crawldad.Api.Features.Webhooks;

/// <summary>Self-registration for the Webhooks slice: the tenant-scoped <see cref="WebhookEndpoint"/> registration
/// document, the register-request validator, the encrypting endpoint store, the outbound-HTTP sender seam, and the bound,
/// boot-validated delivery options. Mirrors the Browsers/Runs module shape. The delivery + fan-out message handlers are
/// discovered by Wolverine; the durable outbox/retry needs no explicit registration here.</summary>
public static class WebhooksModule
{
    /// <summary>Registers the tenant-scoped webhook documents: the <see cref="WebhookEndpoint"/> registration (secret
    /// encrypted at rest; no event stream carries the secret) and the <see cref="WebhookDelivery"/> history row, indexed
    /// on the endpoint it belongs to for the per-endpoint recent-log query and the retention prune. Multi-tenancy comes
    /// from the shared <c>AllDocumentsAreMultiTenanted</c> policy.</summary>
    public static void ConfigureMarten(StoreOptions options)
    {
        options.Schema.For<WebhookEndpoint>();
        options.Schema.For<WebhookDelivery>().Index(delivery => delivery.EndpointName);
    }

    /// <summary>Registers the slice's services: the register validator, the encrypting endpoint store, the HTTP sender
    /// over its SSRF-hardened delivery client, and the bound delivery options with their boot-time validator. Data
    /// Protection (the at-rest cipher) is a host-wide seam the slice leans on.</summary>
    public static void AddWebhooksServices(IServiceCollection services)
    {
        services.AddScoped<IValidator<RegisterWebhookRequest>, RegisterWebhookRequestValidator>();
        services.AddSingleton<IWebhookEndpointStore, MartenWebhookEndpointStore>();
        services.AddSingleton<IWebhookDeliveryStore, MartenWebhookDeliveryStore>();
        services.AddSingleton<IWebhookSender, HttpWebhookSender>();

        // The delivery POST rides a dedicated, SSRF-hardened client: redirects refused and every connection
        // resolve-pinned to a send-time-validated public address (WebhookConnectGuard), so a DNS name that points or
        // rebinds to the platform's own network is refused at send. Named so the guard applies to deliveries only.
        services.AddHttpClient(WebhookHttpClient.Name).ConfigurePrimaryHttpMessageHandler(() => WebhookHttpClient.CreateHandler());

        services.AddOptions<WebhookOptions>().BindConfiguration(WebhookOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<WebhookOptions>, WebhookOptionsValidator>();
    }
}
