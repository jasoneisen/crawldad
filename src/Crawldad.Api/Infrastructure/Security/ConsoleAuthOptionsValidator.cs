using Microsoft.Extensions.Options;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The boot-time guard for the console-auth knobs (bound from <c>Crawldad:ConsoleAuth</c>, registered with
/// <c>ValidateOnStart</c>). Neither knob set is the valid disabled posture (the scheme simply isn't registered); exactly
/// one set is rejected — a half-configured scheme would silently fail to authenticate the portal (an availability failure
/// that looks like an auth bug). When enabled, <see cref="ConsoleAuthOptions.TenantId"/> must be a GUID (the Entra
/// directory id whose issuer/JWKS the scheme trusts) and <see cref="ConsoleAuthOptions.RequiredRole"/> must be non-empty,
/// so the fail-closed AppRole check always has a role to require.</summary>
public sealed class ConsoleAuthOptionsValidator : IValidateOptions<ConsoleAuthOptions>
{
    /// <summary>Validates the bound console-auth knobs, collecting every failure so a misconfigured host reports them together.</summary>
    public ValidateOptionsResult Validate(string? name, ConsoleAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var hasTenant = !string.IsNullOrWhiteSpace(options.TenantId);
        var hasAudience = !string.IsNullOrWhiteSpace(options.Audience);

        // Disabled (neither set) is valid — the scheme is never registered and ApiKey stays the sole/default scheme.
        if (!hasTenant && !hasAudience)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        // Half-configured ⇒ fail closed (mirrors Crawldad:DataProtection's both-or-neither guard).
        if (hasTenant != hasAudience)
        {
            failures.Add("Crawldad:ConsoleAuth needs BOTH TenantId and Audience set (or neither)");
        }

        if (hasTenant && !Guid.TryParse(options.TenantId, out _))
        {
            failures.Add("Crawldad:ConsoleAuth:TenantId must be a GUID (the Entra directory/tenant id)");
        }

        if (string.IsNullOrWhiteSpace(options.RequiredRole))
        {
            failures.Add("Crawldad:ConsoleAuth:RequiredRole must be set when the scheme is enabled");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
