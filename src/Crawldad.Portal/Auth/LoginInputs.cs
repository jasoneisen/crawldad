using System.ComponentModel.DataAnnotations;

namespace Crawldad.Portal.Auth;

/// <summary>The single login form model, bound via <c>[SupplyParameterFromForm]</c> on the login page. The flow
/// is a two-step wizard on one form: <see cref="Step"/> (a hidden field) discriminates the request step from the
/// verify step, so there is never more than one form or one form-bound model in play — no multi-form binding
/// ambiguity. The code is validated by the service (an empty/short code simply fails to match), so it carries no
/// data annotations; the email is validated on both steps.</summary>
public sealed class LoginInput
{
    /// <summary>The request step (enter email).</summary>
    public const string StepRequest = "request";

    /// <summary>The verify step (enter the emailed code).</summary>
    public const string StepVerify = "verify";

    [Required(ErrorMessage = "Enter your email address.")]
    [EmailAddress(ErrorMessage = "That doesn't look like an email address.")]
    public string Email { get; set; } = "";

    public string Code { get; set; } = "";

    public string Step { get; set; } = StepRequest;

    /// <summary>Post-sign-in destination, carried in a hidden field so it survives the two-step form post (the
    /// query string is dropped on submit). Seeded from the <c>?ReturnUrl=</c> the auth middleware adds. Validated
    /// as same-site by <see cref="SafeRedirect"/> before use.</summary>
    public string? ReturnUrl { get; set; }
}
