using Microsoft.Extensions.Options;

namespace Crawldad.Api.Infrastructure.Security;

/// <summary>The boot-time guard for the console-write knobs (bound from <c>Crawldad:ConsoleWrite</c>, registered with
/// <c>ValidateOnStart</c>). Both knobs have generous defaults, so the section may be omitted entirely; when present, a
/// non-positive limit or window is rejected rather than silently disabling the guard (a zero window would divide the sliding
/// count by nothing / never expire).</summary>
public sealed class ConsoleWriteOptionsValidator : IValidateOptions<ConsoleWriteOptions>
{
    /// <summary>Validates the bound console-write knobs, collecting every failure so a misconfigured host reports them together.</summary>
    public ValidateOptionsResult Validate(string? name, ConsoleWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (options.PermitLimit <= 0)
        {
            failures.Add("Crawldad:ConsoleWrite:PermitLimit must be a positive integer");
        }

        if (options.WindowSeconds <= 0)
        {
            failures.Add("Crawldad:ConsoleWrite:WindowSeconds must be a positive integer");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
