using System.Text;
using Crawldad.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Wolverine.Http;

namespace Crawldad.Api.Features.Billing;

/// <summary><c>POST /billing/webhook</c>: the PUBLIC inbound provider endpoint (no tenant key — the provider is not a
/// tenant), so it opts out of the tenant gate via <see cref="AllowAnonymousAttribute"/> and is instead authenticated by
/// the event <b>signature</b>. Order is load-bearing: the raw body is verified <b>before</b> it is parsed; a bad signature
/// changes nothing and is a <c>400</c>. The tenant in the (verified) payload is authoritative — this is the only path that
/// moves a tenant's tier. Registry tenants only: an event for an unknown/env-fallback tenant is logged and dropped (a
/// benign <c>200</c>, so the provider does not retry), as is a replayed event id or a price that maps to no tier.</summary>
public static class BillingWebhookEndpoint
{
    /// <summary>The header carrying the event signature (Stripe's convention), verified by the gateway before parsing.</summary>
    public const string SignatureHeader = "Stripe-Signature";

    /// <summary>Handles <c>POST /billing/webhook</c>.</summary>
    [AllowAnonymous]
    [WolverinePost("/billing/webhook")]
    public static async Task<IResult> Handle(
        HttpContext http,
        IBillingGateway gateway,
        // [FromServices] on the concrete-class services: the body is read manually from HttpContext below (verify before
        // parse), so Wolverine must not treat the first complex parameter as a body to deserialize.
        [FromServices] BillingCatalog catalog,
        ITenantRegistryStore registry,
        IProcessedBillingEventStore processed,
        [FromServices] TenantDirectory directory,
        TimeProvider clock,
        ILogger<BillingWebhookEndpointLog> logger,
        CancellationToken ct)
    {
        var body = await ReadBodyAsync(http.Request, ct);
        var signature = http.Request.Headers[SignatureHeader].ToString();

        // Verify-then-parse, both inside the gateway. A failure here is a bad signature OR an unparseable body — either
        // way nothing is acted on. Never log the body or the signature (they are provider material).
        if (!gateway.TryReadWebhookEvent(body, signature, out var webhookEvent))
        {
            logger.LogWarning("billing webhook rejected: signature invalid or event unparseable");
            return BillingProblems.InvalidWebhook();
        }

        // Anti-replay: the first record wins; a redelivery of the same event id is a no-op acknowledgement.
        if (!await processed.TryRecordAsync(webhookEvent.EventId, clock.GetUtcNow(), ct))
        {
            logger.LogInformation("billing webhook {EventId} already processed; ignoring", webhookEvent.EventId);
            return Results.Ok();
        }

        // Registry tenants only: an env-fallback or unknown tenant is read-only for billing — drop, do not 500.
        if (await registry.FindAsync(webhookEvent.TenantId, ct) is null)
        {
            logger.LogWarning("billing webhook {EventId} for tenant {TenantId} not in the registry; dropping", webhookEvent.EventId, webhookEvent.TenantId);
            return Results.Ok();
        }

        if (!TryResolvePlan(webhookEvent, catalog, out var tier, out var slotAllowance))
        {
            logger.LogWarning("billing webhook {EventId} maps to no known tier (price {PriceId}); dropping", webhookEvent.EventId, webhookEvent.PriceId);
            return Results.Ok();
        }

        await registry.SetPlanAsync(webhookEvent.TenantId, tier, slotAllowance, clock.GetUtcNow(), ct);
        directory.InvalidateTenant(webhookEvent.TenantId); // honour the new slot allowance immediately on this instance
        logger.LogInformation("billing webhook {EventId} set tenant {TenantId} to tier {Tier}", webhookEvent.EventId, webhookEvent.TenantId, tier);
        return Results.Ok();
    }

    // Maps the verified event to the plan to apply: a cancellation → the free tier; a create/update → the tier its price
    // id resolves to. A cancellation with no free tier configured, or a price mapping to no tier, resolves to nothing
    // (the caller drops the event).
    private static bool TryResolvePlan(BillingWebhookEvent webhookEvent, BillingCatalog catalog, out string tier, out int? slotAllowance)
    {
        var mapped = webhookEvent.Change == BillingSubscriptionChange.Cancelled
            ? catalog.ByTier(BillingTierCatalog.FreeTier)
            : catalog.ByPriceId(webhookEvent.PriceId);

        if (mapped is null)
        {
            tier = "";
            slotAllowance = null;
            return false;
        }

        tier = mapped.Tier;
        slotAllowance = mapped.Slots;
        return true;
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }
}

/// <summary>The logger category for <see cref="BillingWebhookEndpoint"/> (a static endpoint class cannot itself be a
/// generic logger's category type).</summary>
public sealed class BillingWebhookEndpointLog;
