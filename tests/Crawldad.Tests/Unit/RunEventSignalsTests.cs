using Crawldad.Web.Features.Runs;

namespace Crawldad.Tests.Unit;

/// <summary>The SSE tail-wakeup hub: a per-run signal completes its <c>Changed</c> task on <see cref="RunSignal.Notify"/> and
/// re-arms; the registry hands one signal per run, no-ops a notify for an unwatched run, and drops a run's slot on remove.
/// It is only a wakeup — correctness comes from the durable re-read — so a missed notify is never a lost event.</summary>
public class RunEventSignalsTests
{
    [Fact]
    public async Task Notify_completes_the_current_changed_task_and_arms_a_fresh_one()
    {
        var signal = new RunSignal();
        var first = signal.Changed;
        first.IsCompleted.ShouldBeFalse();

        signal.Notify();
        await first;

        var second = signal.Changed;
        second.ShouldNotBeSameAs(first);
        second.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public void For_returns_a_stable_signal_per_run()
    {
        var signals = new RunEventSignals();
        var runId = Guid.NewGuid();

        var signal = signals.For(runId);
        signals.For(runId).ShouldBeSameAs(signal);
        signals.For(Guid.NewGuid()).ShouldNotBeSameAs(signal);
    }

    [Fact]
    public async Task Notify_wakes_a_subscribed_run_and_no_ops_an_unwatched_one()
    {
        var signals = new RunEventSignals();
        var watched = Guid.NewGuid();

        signals.Notify(Guid.NewGuid()); // must not throw and creates nothing

        var changed = signals.For(watched).Changed;
        signals.Notify(watched);
        await changed;
    }

    [Fact]
    public void Remove_drops_a_runs_slot()
    {
        var signals = new RunEventSignals();
        var runId = Guid.NewGuid();
        var signal = signals.For(runId);

        signals.Remove(runId);

        signals.For(runId).ShouldNotBeSameAs(signal);
    }
}
