using Microsoft.Extensions.Options;

namespace Crawldad.Api.Features.Webhooks;

/// <summary>The webhook subsystem knobs, bound from <c>Crawldad:Webhooks</c>. Only delivery tuning is configurable; the
/// SSRF target policy and the signature scheme are fixed in code (security invariants, not config).</summary>
public sealed class WebhookOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string Section = "Crawldad:Webhooks";

    /// <summary>The durable-delivery retry/backoff/timeout policy.</summary>
    public WebhookDeliveryOptions Delivery { get; init; } = new();

    /// <summary>The delivery-history retention policy (how much per-endpoint delivery log is kept).</summary>
    public WebhookDeliveryHistoryOptions DeliveryHistory { get; init; } = new();
}

/// <summary>The delivery-history retention policy: how many recent delivery attempts are kept per endpoint. A rolling
/// window, not an audit ledger — after each attempt the endpoint's log is pruned to the newest
/// <see cref="MaxPerEndpoint"/>, so the store stays bounded no matter how chatty a receiver's retries are. Bound the value
/// at boot (see <see cref="WebhookOptionsValidator"/>).</summary>
public sealed class WebhookDeliveryHistoryOptions
{
    /// <summary>The number of most-recent delivery attempts retained per endpoint (older rows are pruned). The default of
    /// 50 comfortably covers a "last delivery" column plus a short drill-down without unbounded growth.</summary>
    public int MaxPerEndpoint { get; init; } = 50;
}

/// <summary>The delivery policy: how many times a POST is attempted, the exponential-backoff schedule between attempts,
/// and the per-attempt HTTP timeout. All bounded so a persistently-failing receiver never retries forever and a slow
/// receiver never ties up a delivery worker. A non-sensical value fails the host loudly at boot (see
/// <see cref="WebhookOptionsValidator"/>).</summary>
public sealed class WebhookDeliveryOptions
{
    /// <summary>The total number of delivery attempts (the first send plus retries) before a delivery is abandoned.</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>The base backoff after the first failed attempt; each further retry doubles it, capped at <see cref="MaxDelay"/>.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The ceiling on the exponential backoff between attempts.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The per-attempt HTTP timeout: a receiver that does not respond within this is a failed attempt (retried).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Boot-time guard for the webhook delivery knobs: a non-positive delay/timeout, a max-attempts below 1, or a
/// max-delay below the base is a misconfiguration that fails the host at startup rather than surfacing as a broken
/// retry loop later. Registered with <c>ValidateOnStart</c>, mirroring <see cref="Crawldad.Api.Features.Runs.RunLimitsOptionsValidator"/>.</summary>
public sealed class WebhookOptionsValidator : IValidateOptions<WebhookOptions>
{
    /// <summary>Validates the bound webhook knobs, collecting every failure so a misconfigured host reports them at once.</summary>
    public ValidateOptionsResult Validate(string? name, WebhookOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        var delivery = options.Delivery;

        Require(failures, delivery.MaxAttempts >= 1, "Delivery:MaxAttempts", "at least 1");
        Require(failures, delivery.BaseDelay > TimeSpan.Zero, "Delivery:BaseDelay", "positive");
        Require(failures, delivery.MaxDelay >= delivery.BaseDelay, "Delivery:MaxDelay", "at least Delivery:BaseDelay");
        Require(failures, delivery.Timeout > TimeSpan.Zero, "Delivery:Timeout", "positive");
        Require(failures, options.DeliveryHistory.MaxPerEndpoint >= 1, "DeliveryHistory:MaxPerEndpoint", "at least 1");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(List<string> failures, bool ok, string knob, string expectation)
    {
        if (!ok)
        {
            failures.Add($"Crawldad:Webhooks:{knob} must be {expectation}");
        }
    }
}
