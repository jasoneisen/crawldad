using Microsoft.Extensions.Options;

namespace Crawldad.Portal.Infrastructure.Security;

/// <summary>The boot-time guard for the portal's console-auth knobs (bound from <c>Crawldad:ConsoleAuth</c>, registered
/// with <c>ValidateOnStart</c>). Neither knob set is the valid disabled posture (console-mode simply isn't wired); exactly
/// one set is rejected — a half-configured portal would try to acquire tokens with no audience (or claim console-mode with
/// no directory). When enabled, <see cref="PortalConsoleAuthOptions.TenantId"/> must be a GUID (the Entra directory id).</summary>
public sealed class PortalConsoleAuthOptionsValidator : IValidateOptions<PortalConsoleAuthOptions>
{
    /// <summary>Validates the bound console-auth knobs, collecting every failure so a misconfigured host reports them together.</summary>
    public ValidateOptionsResult Validate(string? name, PortalConsoleAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var hasTenant = !string.IsNullOrWhiteSpace(options.TenantId);
        var hasAudience = !string.IsNullOrWhiteSpace(options.Audience);

        // Disabled (neither set) is valid — console-mode is never wired and data pages show "console access not configured".
        if (!hasTenant && !hasAudience)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        // Half-configured ⇒ fail closed (mirrors Crawldad:Portal:DataProtection's both-or-neither guard).
        if (hasTenant != hasAudience)
        {
            failures.Add("Crawldad:ConsoleAuth needs BOTH TenantId and Audience set (or neither)");
        }

        if (hasTenant && !Guid.TryParse(options.TenantId, out _))
        {
            failures.Add("Crawldad:ConsoleAuth:TenantId must be a GUID (the Entra directory/tenant id)");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
