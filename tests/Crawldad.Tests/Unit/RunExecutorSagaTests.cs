using Crawldad.Api.Features.Runs;
using Wolverine;

namespace Crawldad.Tests.Unit;

/// <summary>The <see cref="RunExecutorSaga"/> orchestration logic in isolation: the idempotent saga starter
/// (<see cref="RunExecutorSaga.StartOrHandle"/>) makes a redelivered <c>StartRun</c> a no-op; <see cref="RunDeadlineHandler"/>
/// stops a still-running run at its wall-clock deadline without touching the saga. Wolverine wiring is covered by <c>DurableRunTests</c>.</summary>
public class RunExecutorSagaTests
{
    private static StartRun Command(Guid runId) =>
        new(runId, "payload.name", "scripthash", """{ "crawldad": "1" }""", """{ "k": "v" }""", null, null, DeadlineMs: 90_000);

    [Fact]
    public void StartOrHandle_pins_the_definition_and_cascades_execute_plus_a_delayed_deadline_on_a_fresh_start()
    {
        var runId = Guid.NewGuid();
        var saga = new RunExecutorSaga();

        var outgoing = saga.StartOrHandle(Command(runId));

        // The saga's document id is the run id — Wolverine's maybe-existing codegen does NOT set it, so the starter must,
        // else the executor's ExecuteRun could never load the saga (the codegen-mismatch bug this pins against regression).
        saga.Id.ShouldBe(runId);
        saga.PayloadName.ShouldBe("payload.name");
        saga.ScriptHash.ShouldBe("scripthash");
        saga.Script.ShouldBe("""{ "crawldad": "1" }""");
        saga.Inputs.ShouldBe("""{ "k": "v" }""");

        outgoing.OfType<ExecuteRun>().Single().RunId.ShouldBe(runId);
        var deadline = outgoing.OfType<DeliveryMessage<RunDeadline>>().Single();
        deadline.Message.RunId.ShouldBe(runId);
        deadline.Options.ScheduleDelay.ShouldBe(TimeSpan.FromMilliseconds(90_000));
    }

    [Fact]
    public void StartOrHandle_is_a_no_op_when_the_saga_has_already_started()
    {
        var runId = Guid.NewGuid();
        var saga = new RunExecutorSaga();
        saga.StartOrHandle(Command(runId));

        // A redelivered / duplicate StartRun loads the already-started saga (Wolverine's load-first path): re-cascade nothing,
        // so no second ExecuteRun, no second deadline, and — crucially — no duplicate-key insert (a genuine no-op).
        var redelivered = saga.StartOrHandle(Command(runId));

        redelivered.ShouldBeEmpty();
        saga.Id.ShouldBe(runId);
    }

    [Fact]
    public void StartOrHandle_ignores_a_redelivery_carrying_different_content()
    {
        var runId = Guid.NewGuid();
        var saga = new RunExecutorSaga();
        saga.StartOrHandle(Command(runId));

        // Even a redelivery whose body somehow differs must not mutate an already-started saga's pinned definition.
        var redelivered = saga.StartOrHandle(new StartRun(runId, "other", "otherhash", "{}", "{}", Guid.NewGuid(), 7, 1));

        redelivered.ShouldBeEmpty();
        saga.PayloadName.ShouldBe("payload.name");
        saga.ScriptHash.ShouldBe("scripthash");
    }

    [Fact]
    public void RunDeadline_stops_a_still_running_run_via_its_control()
    {
        var runId = Guid.NewGuid();
        var controls = new RunControlRegistry();
        var control = controls.GetOrAdd(runId);

        RunDeadlineHandler.Handle(new RunDeadline(runId), controls);

        control.StopReason.ShouldBe(RunStopReason.Deadline); // the executor then finalises a terminal run_deadline_exceeded
    }

    [Fact]
    public void RunDeadline_for_a_finished_run_whose_control_is_gone_is_a_no_op() =>
        RunDeadlineHandler.Handle(new RunDeadline(Guid.NewGuid()), new RunControlRegistry()); // spent deadline — must not throw
}
