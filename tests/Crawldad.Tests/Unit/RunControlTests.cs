using Crawldad.Web.Features.Runs;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The in-process run stop-signal (<see cref="RunControl"/>, §11): the executor claims a run exactly once, a cancel/deadline
/// stops it (first reason wins), and only a deadline forcibly cancels the bound source — a user cancel stays cooperative.
/// </summary>
public class RunControlTests
{
    [Fact]
    public void TryClaim_succeeds_once_then_fails()
    {
        var control = new RunControl();

        control.TryClaim().ShouldBeTrue();  // the first executor claims the run
        control.TryClaim().ShouldBeFalse(); // a redelivered/recovered executor for the same run is turned away
    }

    [Fact]
    public void A_fresh_control_is_not_stopped()
    {
        var control = new RunControl();

        control.StopRequested.ShouldBeFalse();
        control.StopReason.ShouldBeNull();
    }

    [Fact]
    public void The_first_stop_reason_wins()
    {
        var control = new RunControl();

        control.Stop(RunStopReason.Cancelled);
        control.Stop(RunStopReason.Deadline); // a later deadline never overrides a user cancel already in flight

        control.StopRequested.ShouldBeTrue();
        control.StopReason.ShouldBe(RunStopReason.Cancelled);
    }

    [Fact]
    public void A_deadline_stop_forcibly_cancels_the_bound_source()
    {
        var control = new RunControl();
        using var forcible = new CancellationTokenSource();
        control.UseForcibleCancellation(forcible);

        control.Stop(RunStopReason.Deadline);

        forcible.IsCancellationRequested.ShouldBeTrue(); // a stuck run is interrupted mid-call
    }

    [Fact]
    public void A_user_cancel_stop_leaves_the_bound_source_uncancelled()
    {
        var control = new RunControl();
        using var forcible = new CancellationTokenSource();
        control.UseForcibleCancellation(forcible);

        control.Stop(RunStopReason.Cancelled);

        forcible.IsCancellationRequested.ShouldBeFalse(); // a user cancel is honoured cooperatively, never yanked mid-step
    }

    [Fact]
    public void A_deadline_stop_without_a_bound_source_is_a_no_op()
    {
        var control = new RunControl();

        control.Stop(RunStopReason.Deadline); // no forcible source bound — must not throw

        control.StopReason.ShouldBe(RunStopReason.Deadline);
    }
}
