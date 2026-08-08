using Crawldad.Web.Features.Runs;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The <see cref="RunExecutorSaga"/> orchestration logic in isolation (§14.2, CD-5): the idempotent saga starter
/// (<see cref="RunExecutorSaga.StartOrHandle"/>) that makes a redelivered <c>StartRun</c> a no-op, and the prompt
/// <see cref="RunFinished"/> completion (+ its not-found no-op) that reclaims a finished run's saga. The Wolverine-wired
/// behaviour (load-first codegen, tenancy, the <c>RunDeadline</c> janitor) is exercised end-to-end in <c>DurableRunTests</c>;
/// these are the pure-state guarantees underneath.
/// </summary>
public class RunExecutorSagaTests
{
    private static StartRun Command(Guid runId) =>
        new(runId, "payload.name", "scripthash", """{ "crawldad": "1" }""", """{ "k": "v" }""", null, null, DeadlineMs: 90_000);

    [Fact]
    public void StartOrHandle_pins_the_definition_and_cascades_execute_plus_deadline_on_a_fresh_start()
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

        // The fresh start kicks the durable executor and schedules the wall-clock deadline from here.
        outgoing.OfType<ExecuteRun>().Single().RunId.ShouldBe(runId);
        var deadline = outgoing.OfType<RunDeadline>().Single();
        deadline.RunId.ShouldBe(runId);
        deadline.DeadlineDelay.ShouldBe(TimeSpan.FromMilliseconds(90_000));
    }

    [Fact]
    public void StartOrHandle_is_a_no_op_when_the_saga_has_already_started()
    {
        var runId = Guid.NewGuid();
        var saga = new RunExecutorSaga();
        saga.StartOrHandle(Command(runId)); // first delivery — starts

        // A redelivered / duplicate StartRun loads the already-started saga (Wolverine's load-first path): re-cascade nothing,
        // so no second ExecuteRun, no second deadline timer, and — crucially — no duplicate-key insert (a genuine no-op).
        var redelivered = saga.StartOrHandle(Command(runId));

        redelivered.ShouldBeEmpty();
        saga.Id.ShouldBe(runId); // unchanged
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
    public void RunFinished_marks_the_saga_completed()
    {
        var saga = new RunExecutorSaga();
        saga.StartOrHandle(Command(Guid.NewGuid()));
        saga.IsCompleted().ShouldBeFalse();

        saga.Handle(new RunFinished(saga.Id));

        saga.IsCompleted().ShouldBeTrue(); // Wolverine deletes the saga document at the end of the message
    }

    [Fact]
    public void NotFound_for_a_run_finished_whose_saga_is_gone_is_a_no_op() =>
        RunExecutorSaga.NotFound(new RunFinished(Guid.NewGuid())); // must not throw — the idempotent already-completed path
}
