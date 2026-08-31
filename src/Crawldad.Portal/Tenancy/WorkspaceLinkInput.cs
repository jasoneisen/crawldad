using System.ComponentModel.DataAnnotations;

namespace Crawldad.Portal.Tenancy;

/// <summary>The "claim an existing workspace" form model, bound via <c>[SupplyParameterFromForm]</c> on the account page.
/// Both fields are required; the API key is write-only — it is posted and validated (then a membership is recorded and the
/// key is DISCARDED, never stored), and it is never re-rendered back into the form, so the value round-trips one way only.</summary>
public sealed class WorkspaceLinkInput
{
    /// <summary>The workspace / tenant id the account holder is linking to. Confirmed against the API key on submit.</summary>
    [Required(ErrorMessage = "Enter your workspace ID.")]
    public string TenantId { get; set; } = "";

    /// <summary>The tenant API key. Treated like a password: never echoed back to the browser or logged.</summary>
    [Required(ErrorMessage = "Enter your API key.")]
    public string ApiKey { get; set; } = "";
}
