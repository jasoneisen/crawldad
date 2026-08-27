using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Crawldad.Api.Features.Billing;

/// <summary>The development/test <see cref="IBillingGateway"/>: deterministic and in-process, touching no live provider.
/// It is always <see cref="IsConfigured"/> (billing "works" locally); session calls return an in-app
/// <c>/app/account/billing-result</c> URL (the portal, being the redirect origin, resolves a relative one against
/// itself); and the webhook verifier is a plain shared-token equality check — a genuine fake, not real crypto — over a
/// small JSON envelope the tests post. Verification happens before the body is parsed.</summary>
internal sealed class FakeBillingGateway(IOptions<BillingOptions> options) : IBillingGateway
{
    private readonly BillingOptions _options = options.Value;

    /// <summary>The accepted webhook signature when no <c>Billing:Stripe:WebhookSecret</c> is configured — so the fake
    /// verifier still has a definite "valid" value to compare against locally.</summary>
    public const string DefaultSignature = "fake-valid-signature";

    /// <summary>The portal route the fake redirects back to. A deliberate, flagged scaffolding coupling: the real gateway
    /// will build absolute provider URLs from <see cref="BillingOptions.PortalReturnUrl"/> instead of hardcoding a path.</summary>
    public const string ResultPath = "/app/account/billing-result";

    public bool IsConfigured => true;

    public Task<BillingSession> CreateCheckoutSessionAsync(string tenantId, BillingTierConfig tier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tier);
        var url = ResultUrl($"outcome=checkout&tier={Uri.EscapeDataString(tier.Tier)}&session=cs_fake_{Guid.NewGuid():N}");
        return Task.FromResult(new BillingSession(url));
    }

    public Task<BillingSession> CreatePortalSessionAsync(string tenantId, CancellationToken ct) =>
        Task.FromResult(new BillingSession(ResultUrl($"outcome=portal&session=ps_fake_{Guid.NewGuid():N}")));

    public bool TryReadWebhookEvent(string rawBody, string? signatureHeader, out BillingWebhookEvent webhookEvent)
    {
        webhookEvent = null!;

        // Verify BEFORE parsing: a wrong/absent signature is rejected without the body ever being read as an event.
        var expected = string.IsNullOrEmpty(_options.Stripe.WebhookSecret) ? DefaultSignature : _options.Stripe.WebhookSecret;
        if (!string.Equals(signatureHeader, expected, StringComparison.Ordinal))
        {
            return false;
        }

        return TryParse(rawBody, out webhookEvent);
    }

    // Builds an in-app result URL: relative when no PortalReturnUrl is configured (the portal resolves it against its own
    // origin), else absolute under that base.
    private string ResultUrl(string query) => $"{_options.PortalReturnUrl.TrimEnd('/')}{ResultPath}?{query}";

    // Parses the small fake event envelope: { "id", "type", "tenant", "priceId" }. Returns false (act on nothing) for a
    // malformed body, a missing id/tenant, or an unrecognised type — all "cannot act" cases the endpoint turns into a 400.
    private static bool TryParse(string rawBody, out BillingWebhookEvent webhookEvent)
    {
        webhookEvent = null!;
        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return false;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object
            || ReadString(root, "id") is not { } id
            || ReadString(root, "type") is not { } type
            || ReadString(root, "tenant") is not { } tenant
            || MapChange(type) is not { } change)
        {
            return false;
        }

        webhookEvent = new BillingWebhookEvent(id, change, tenant, ReadString(root, "priceId"));
        return true;
    }

    // A non-empty string property, or null when absent/blank/wrong-kinded.
    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : null;

    // Maps the provider event-type string to the normalized change, or null for a type the billing flow does not act on.
    private static BillingSubscriptionChange? MapChange(string type) => type switch
    {
        "customer.subscription.created" => BillingSubscriptionChange.Created,
        "customer.subscription.updated" => BillingSubscriptionChange.Updated,
        "customer.subscription.deleted" => BillingSubscriptionChange.Cancelled,
        _ => null,
    };
}
