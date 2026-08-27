namespace Crawldad.Portal.Components.Pages.App;

/// <summary>The register-endpoint form model (static-SSR form post on the Webhooks page). All fields are nullable so a
/// blank submit round-trips as empty rather than tripping the framework binder; the page validates required fields and
/// delegates deep validation (slug shape, https/SSRF policy, secret length, event catalog) to the API. The
/// <see cref="Secret"/> is write-only in the UI — the page clears it after building the request so it is never rendered
/// back into the field.</summary>
public sealed class RegisterWebhookInput
{
    /// <summary>The endpoint name (a slug; becomes the endpoint id).</summary>
    public string? Name { get; set; }

    /// <summary>The delivery target URL.</summary>
    public string? Url { get; set; }

    /// <summary>The HMAC signing secret (write-only — never echoed back).</summary>
    public string? Secret { get; set; }

    /// <summary>Subscribe to <c>run.succeeded</c>.</summary>
    public bool Succeeded { get; set; }

    /// <summary>Subscribe to <c>run.failed</c>.</summary>
    public bool Failed { get; set; }

    /// <summary>Subscribe to <c>run.cancelled</c>.</summary>
    public bool Cancelled { get; set; }
}

/// <summary>The deregister-confirm form model: just the endpoint name to delete, carried in a hidden field on the
/// server-rendered confirmation.</summary>
public sealed class DeleteWebhookInput
{
    /// <summary>The endpoint name to deregister.</summary>
    public string? Name { get; set; }
}
