using Microsoft.Extensions.Options;

namespace Crawldad.Web.Features.Runs;

/// <summary>The boot-time guard for the global resource-limit knobs: a nonsensical value (a zero or negative cap, a
/// negative queue wait) is a misconfiguration that should fail the host <b>loudly at startup</b>, not surface later as
/// a run that can never be admitted. Registered with <c>ValidateOnStart</c>, mirroring <see cref="Crawldad.Web.Infrastructure.Security.TenantRegistry"/>'s boot validation of the per-tenant overrides.</summary>
public sealed class RunLimitsOptionsValidator : IValidateOptions<RunLimitsOptions>
{
    /// <summary>Validates the bound global knobs, collecting every failure so a misconfigured host reports them all at once.</summary>
    public ValidateOptionsResult Validate(string? name, RunLimitsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        Require(failures, options.MaxStepsPerRun >= 1, nameof(options.MaxStepsPerRun), "at least 1");
        Require(failures, options.MaxDownloadedBytesPerRun >= 1, nameof(options.MaxDownloadedBytesPerRun), "at least 1");
        Require(failures, options.MaxCapturedBytesPerRun >= 1, nameof(options.MaxCapturedBytesPerRun), "at least 1");
        Require(failures, options.MaxEventsPerRun >= 1, nameof(options.MaxEventsPerRun), "at least 1");
        Require(failures, options.ExpressionStepBudget >= 1, nameof(options.ExpressionStepBudget), "at least 1");
        Require(failures, options.MaxConcurrentRunsPerTenant >= 1, nameof(options.MaxConcurrentRunsPerTenant), "at least 1");
        Require(failures, options.MaxQueueDepthPerTenant >= 1, nameof(options.MaxQueueDepthPerTenant), "at least 1");
        Require(failures, options.MaxQueueWaitMs >= 0, nameof(options.MaxQueueWaitMs), "0 or greater (0 disables the bound)");
        Require(failures, options.SyncUpgradeThresholdMs >= 0, nameof(options.SyncUpgradeThresholdMs), "0 or greater (0 upgrades every sync run immediately)");

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
