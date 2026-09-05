using Crawldad.Api.Features.Runs;

namespace Crawldad.Tests.Unit;

/// <summary>The boot-time guard for the global resource-limit knobs: a nonsensical <c>Crawldad:Limits</c> value must
/// fail the host loudly at startup rather than surface as a run that can never be admitted or a queue that rejects everything.
/// Proves the defaults pass and each knob's floor is enforced, with every failure collected (not just the first).</summary>
public class RunLimitsOptionsValidatorTests
{
    private static readonly RunLimitsOptionsValidator _validator = new();

    [Fact]
    public void Accepts_the_generous_defaults() =>
        _validator.Validate(name: null, new RunLimitsOptions()).Succeeded.ShouldBeTrue();

    [Fact]
    public void Accepts_a_disabled_queue_wait() => // 0 is the sentinel for "no bound", not an invalid value
        _validator.Validate(name: null, new RunLimitsOptions { MaxQueueWaitMs = 0 }).Succeeded.ShouldBeTrue();

    [Fact]
    public void Accepts_an_immediate_sync_upgrade() => // 0 upgrades every sync run immediately (async-only), a valid posture
        _validator.Validate(name: null, new RunLimitsOptions { SyncUpgradeThresholdMs = 0 }).Succeeded.ShouldBeTrue();

    [Fact]
    public void Accepts_a_disabled_shutdown_drain() => // 0 means "no drain window", a valid (if unforgiving) posture
        _validator.Validate(name: null, new RunLimitsOptions { ShutdownDrainMs = 0 }).Succeeded.ShouldBeTrue();

    [Fact]
    public void Rejects_a_negative_shutdown_drain()
    {
        var result = _validator.Validate(name: null, new RunLimitsOptions { ShutdownDrainMs = -1 });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(nameof(RunLimitsOptions.ShutdownDrainMs));
    }

    [Fact]
    public void Rejects_a_negative_sync_upgrade_threshold()
    {
        var result = _validator.Validate(name: null, new RunLimitsOptions { SyncUpgradeThresholdMs = -1 });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(nameof(RunLimitsOptions.SyncUpgradeThresholdMs));
    }

    [Theory]
    [InlineData(nameof(RunLimitsOptions.MaxStepsPerRun))]
    [InlineData(nameof(RunLimitsOptions.MaxDownloadedBytesPerRun))]
    [InlineData(nameof(RunLimitsOptions.MaxCapturedBytesPerRun))]
    [InlineData(nameof(RunLimitsOptions.MaxEventsPerRun))]
    [InlineData(nameof(RunLimitsOptions.ExpressionStepBudget))]
    [InlineData(nameof(RunLimitsOptions.MaxConcurrentRunsPerTenant))]
    [InlineData(nameof(RunLimitsOptions.MaxQueueDepthPerTenant))]
    public void Rejects_a_below_one_knob(string knob)
    {
        var result = _validator.Validate(name: null, Bad(knob));

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(knob);
    }

    [Fact]
    public void Rejects_a_negative_queue_wait()
    {
        var result = _validator.Validate(name: null, new RunLimitsOptions { MaxQueueWaitMs = -1 });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(nameof(RunLimitsOptions.MaxQueueWaitMs));
    }

    [Fact]
    public void Collects_every_failure_at_once()
    {
        var result = _validator.Validate(name: null, new RunLimitsOptions { MaxConcurrentRunsPerTenant = 0, MaxQueueDepthPerTenant = 0 });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(nameof(RunLimitsOptions.MaxConcurrentRunsPerTenant));
        result.FailureMessage.ShouldContain(nameof(RunLimitsOptions.MaxQueueDepthPerTenant));
    }

    // A RunLimitsOptions with exactly one knob set below its floor (the others at their valid defaults).
    private static RunLimitsOptions Bad(string knob) => knob switch
    {
        nameof(RunLimitsOptions.MaxStepsPerRun) => new RunLimitsOptions { MaxStepsPerRun = 0 },
        nameof(RunLimitsOptions.MaxDownloadedBytesPerRun) => new RunLimitsOptions { MaxDownloadedBytesPerRun = 0 },
        nameof(RunLimitsOptions.MaxCapturedBytesPerRun) => new RunLimitsOptions { MaxCapturedBytesPerRun = 0 },
        nameof(RunLimitsOptions.MaxEventsPerRun) => new RunLimitsOptions { MaxEventsPerRun = 0 },
        nameof(RunLimitsOptions.ExpressionStepBudget) => new RunLimitsOptions { ExpressionStepBudget = 0 },
        nameof(RunLimitsOptions.MaxConcurrentRunsPerTenant) => new RunLimitsOptions { MaxConcurrentRunsPerTenant = 0 },
        _ => new RunLimitsOptions { MaxQueueDepthPerTenant = 0 },
    };
}
