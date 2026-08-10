using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit;

/// <summary>The mid-run resource limits, each a terminal failure with its own code when a run outruns a server-side
/// cap the payload cannot raise: max steps, max downloaded bytes, max event count, and the per-evaluation expression
/// fuel budget. Mid-run counters reset per segment across a checkpoint resume; the concurrent-runs cap is admission-time.</summary>
public class ResourceLimitsTests
{
    private const string _capHomeUrl = "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx?module=Enforcement&TabName=Enforcement";

    private const string _downloadInputs =
        """{ "backend": { "adapter": "fake", "options": { "fixture": "download-sample" } }, "attachmentStore": { "kind": "fake", "name": "attachmentStore" } }""";

    private static string DownloadPayload() =>
        File.ReadAllText(Path.Combine(Runner.FixturesRoot, "Payloads", "download-fragment.json"));

    // ----- limit 1: max steps per run ----------------------------------------

    [Fact]
    public async Task A_run_that_outruns_its_max_steps_cap_fails_terminally()
    {
        // An unbounded-by-design body (its per-loop maxIterations is generous) — only the global step cap stops it.
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": { "n": 0 },
              "steps": [ { "loop": { "maxIterations": 1000000, "while": "true", "do": [ { "set": { "var": "n", "value": "n + 1" } } ] } } ],
              "result": "n" }
            """;

        var (outcome, _) = await Runner.RunWithFakeAsync(Payload, limits: RunLimits.Default with { MaxSteps = 25 });

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Class.ShouldBe("terminal");
        outcome.Failure.Code.ShouldBe(InterpreterErrorCodes.MaxStepsExceeded);
        outcome.Stats.Steps.ShouldBe(26); // stopped the first step past the cap
    }

    [Fact]
    public async Task A_run_within_its_max_steps_cap_succeeds()
    {
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": { "n": 0 },
              "steps": [ { "loop": { "maxIterations": 5, "for": { "var": "i", "from": "0", "to": "3" }, "do": [ { "set": { "var": "n", "value": "n + 1" } } ] } } ],
              "result": "n" }
            """;

        var (outcome, _) = await Runner.RunWithFakeAsync(Payload, limits: RunLimits.Default with { MaxSteps = 25 });

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        outcome.Result!.Value.GetInt64().ShouldBe(3);
    }

    // ----- limit 2: max total downloaded bytes per run -----------------------

    [Fact]
    public async Task A_single_download_over_the_byte_cap_fails_as_the_bytes_flow()
    {
        // The sample download is 30 bytes; a 10-byte cap trips mid-stream on the first download, never buffering the body.
        var (outcome, _) = await Runner.RunWithFakeAsync(DownloadPayload(), _downloadInputs, limits: RunLimits.Default with { MaxDownloadedBytes = 10 });

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Class.ShouldBe("terminal");
        outcome.Failure.Code.ShouldBe(InterpreterErrorCodes.MaxDownloadBytesExceeded);
    }

    [Fact]
    public async Task Downloaded_bytes_accumulate_across_downloads_in_a_run()
    {
        // Two 30-byte downloads (60 total). A 40-byte cap admits the first (30 ≤ 40) and trips on the second (60 > 40) —
        // proving the cap is the run-wide total, not per-download.
        var (outcome, _) = await Runner.RunWithFakeAsync(DownloadPayload(), _downloadInputs, limits: RunLimits.Default with { MaxDownloadedBytes = 40 });

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe(InterpreterErrorCodes.MaxDownloadBytesExceeded);
        outcome.Stats.Downloads.ShouldBe(1); // the first download completed; the second aborted mid-stream
    }

    // ----- limit 3: max event count per run ----------------------------------

    [Fact]
    public async Task A_run_that_appends_more_events_than_its_cap_fails_terminally()
    {
        // The durable path (observer present) emits a step-trace event per action; a tight event cap trips inside the loop.
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": { "acc": [] },
              "steps": [ { "loop": { "maxIterations": 1000, "for": { "var": "i", "from": "0", "to": "50" }, "do": [ { "set": { "var": "n", "value": "i" } } ] } } ],
              "result": "acc" }
            """;

        var (outcome, _, _) = await Runner.RunWithObserverAsync(Payload, limits: RunLimits.Default with { MaxEvents = 5 });

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Class.ShouldBe("terminal");
        outcome.Failure.Code.ShouldBe(InterpreterErrorCodes.MaxEventsExceeded);
    }

    [Fact]
    public async Task The_synchronous_path_does_not_count_step_events_against_the_cap()
    {
        // No observer ⇒ no step-trace events, so even a very low event cap does not bite a run whose stream carries only the
        // coarse events (here none) — the sync stream is byte-identical to before.
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": {},
              "steps": [ { "loop": { "maxIterations": 1000, "for": { "var": "i", "from": "0", "to": "20" }, "do": [ { "set": { "var": "n", "value": "i" } } ] } } ],
              "result": "'ok'" }
            """;

        var (outcome, _) = await Runner.RunWithFakeAsync(Payload, limits: RunLimits.Default with { MaxEvents = 1 });

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        outcome.Result!.Value.GetString().ShouldBe("ok");
    }

    // ----- limit 4: expression evaluation step budget ------------------------

    [Fact]
    public async Task A_pathological_expression_that_outspends_its_fuel_budget_fails_terminally()
    {
        // A single breadth-heavy result expression (an array literal wider than the budget) trips the per-evaluation fuel —
        // the backend-resolution expression (2 nodes) stays under the same tiny budget, so setup still succeeds.
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": {}, "steps": [],
              "result": "[1,2,3,4,5,6,7,8,9,10,11,12]" }
            """;

        var (outcome, _) = await Runner.RunWithFakeAsync(Payload, limits: RunLimits.Default with { ExpressionStepBudget = 5 });

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Class.ShouldBe("terminal");
        outcome.Failure.Code.ShouldBe(ExpressionErrorCodes.ExpressionBudgetExceeded);
    }

    [Fact]
    public async Task The_expression_budget_is_per_evaluation_not_cumulative_across_a_run()
    {
        // Many small expressions in sequence never accumulate against the fuel — each starts fresh — so a run of many
        // modest expressions passes under a budget no single one exceeds.
        const string Payload =
            """
            { "name": "t", "config": { "backend": "input.backend" }, "vars": { "acc": [] },
              "steps": [ { "loop": { "maxIterations": 1000, "for": { "var": "i", "from": "0", "to": "40" }, "do": [ { "push": { "into": "acc", "value": "i + 1" } } ] } } ],
              "result": "count(acc)" }
            """;

        var (outcome, _) = await Runner.RunWithFakeAsync(Payload, limits: RunLimits.Default with { ExpressionStepBudget = 50 });

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        outcome.Result!.Value.GetInt64().ShouldBe(40);
    }

    // ----- mid-run counters reset per segment across a checkpoint resume -----

    [Fact]
    public async Task Mid_run_step_counter_resets_on_checkpoint_resume()
    {
        // A checkpointing run: step 0 navigates, step 1 is a loop whose body checkpoints then advances a counter.
        var payload = ResumePayload();

        // A full fresh run establishes the whole-run step count and yields the checkpoints we resume from.
        var (fresh, observer, _) = await Runner.RunWithObserverAsync(payload);
        fresh.Status.ShouldBe(RunStatus.Succeeded, fresh.Failure?.Code);
        observer.Checkpoints.Count.ShouldBeGreaterThan(1);

        // Resume from the second checkpoint (mid-run): a fresh interpreter re-enters at the checkpoint's step, restoring the
        // var snapshot but NOT the step counter.
        var snapshot = observer.Checkpoints[1];
        var resume = new ResumeState(snapshot.Name, snapshot.Sequence, snapshot.StepIndex, snapshot.Cursor, snapshot.Vars);
        var (resumed, _, _) = await Runner.RunWithObserverAsync(payload, resume: resume);
        resumed.Status.ShouldBe(RunStatus.Succeeded, resumed.Failure?.Code);

        // The resumed segment ran strictly fewer steps than the whole run (it skips the pre-checkpoint work) — the pre-
        // checkpoint steps would have carried into the resumed count had the counter not reset.
        resumed.Stats.Steps.ShouldBeLessThan(fresh.Stats.Steps);
        JsonAssert.Canonical(resumed.Result!.Value).ShouldBe(JsonAssert.Canonical(fresh.Result!.Value)); // same final result

        // The cap set to exactly the resumed segment's step count: the resumed run (counter reset to 0) fits under it and
        // succeeds, while the whole fresh run — more steps than the cap — trips. Only a reset counter explains both.
        var segmentCap = RunLimits.Default with { MaxSteps = resumed.Stats.Steps };
        (await Runner.RunWithObserverAsync(payload, limits: segmentCap, resume: resume)).Outcome
            .Status.ShouldBe(RunStatus.Succeeded);
        var freshCapped = (await Runner.RunWithObserverAsync(payload, limits: segmentCap)).Outcome;
        freshCapped.Status.ShouldBe(RunStatus.Failed);
        freshCapped.Failure!.Code.ShouldBe(InterpreterErrorCodes.MaxStepsExceeded);
    }

    private static string ResumePayload() =>
        $$"""
        { "name": "resume.reset", "config": { "backend": "input.backend" }, "vars": { "acc": [], "n": 0 },
          "steps": [
            { "goto": { "url": "{{_capHomeUrl}}" } },
            { "loop": { "maxIterations": 100, "while": "n < 4", "do": [
                { "checkpoint": { "name": "page", "cursor": "{ n: n }", "resume": [ { "goto": { "url": "{{_capHomeUrl}}" } } ] } },
                { "set": { "var": "n", "value": "n + 1" } },
                { "push": { "into": "acc", "value": "n" } }
            ] } }
          ],
          "result": "acc" }
        """;
}
