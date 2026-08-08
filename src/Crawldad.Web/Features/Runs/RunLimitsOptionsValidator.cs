using Microsoft.Extensions.Options;

namespace Crawldad.Web.Features.Runs;

/// <summary>
/// The boot-time guard for the global resource-limit knobs (CD-3/CD-16 NIT): <see cref="RunLimitsOptions"/> is bound from
/// <c>Crawldad:Limits</c>, and a nonsensical value (a zero or negative cap, a negative queue wait) is a misconfiguration that
/// should fail the host <b>loudly at startup</b> rather than surface as a run that can never be admitted or a queue that
/// rejects everything. Registered with <c>ValidateOnStart</c>, so it runs once at boot. The per-tenant overrides are already
/// boot-validated in <see cref="Crawldad.Web.Infrastructure.Security.TenantRegistry"/>'s constructor; this closes the same gap
/// for the global defaults those overrides fall back to.
/// </summary>
public sealed class RunLimitsOptionsValidator : IValidateOptions<RunLimitsOptions>
{
    /// <summary>Validates the bound global knobs, collecting every failure so a misconfigured host reports them all at once.</summary>
    /// <param name="name">The options name (unused — there is a single unnamed instance).</param>
    /// <param name="options">The bound options.</param>
    /// <returns>Success, or a failure listing each invalid knob.</returns>
    public ValidateOptionsResult Validate(string? name, RunLimitsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        Require(failures, options.MaxStepsPerRun >= 1, nameof(options.MaxStepsPerRun), "at least 1");
        Require(failures, options.MaxDownloadedBytesPerRun >= 1, nameof(options.MaxDownloadedBytesPerRun), "at least 1");
        Require(failures, options.MaxEventsPerRun >= 1, nameof(options.MaxEventsPerRun), "at least 1");
        Require(failures, options.ExpressionStepBudget >= 1, nameof(options.ExpressionStepBudget), "at least 1");
        Require(failures, options.MaxConcurrentRunsPerTenant >= 1, nameof(options.MaxConcurrentRunsPerTenant), "at least 1");
        Require(failures, options.MaxQueueDepthPerTenant >= 1, nameof(options.MaxQueueDepthPerTenant), "at least 1");
        Require(failures, options.MaxQueueWaitMs >= 0, nameof(options.MaxQueueWaitMs), "0 or greater (0 disables the bound)");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(List<string> failures, bool ok, string knob, string expectation)
    {
        if (!ok)
        {
            failures.Add($"Crawldad:Limits:{knob} must be {expectation}");
        }
    }
}
