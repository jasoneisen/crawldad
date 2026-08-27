using Crawldad.Api.Features.Runs.Interpreter;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Unit;

/// <summary>Payload parse-hardening: unknown head keys and missing <c>maxIterations</c> — even deep inside a
/// nested block — are rejected at parse time, before any step executes, so a malformed payload fails with no
/// side effects (zero steps).</summary>
public class PayloadValidationTests
{
    private static string Payload(string steps) =>
        $$"""{ "name": "t", "config": { "backend": "input.backend" }, "vars": {}, "steps": {{steps}}, "result": "null" }""";

    [Fact]
    public async Task Unknown_head_key_deep_in_a_nested_block_fails_at_parse_with_zero_steps()
    {
        var outcome = await Runner.RunAsync(Payload(
            """[ { "set": { "var": "x", "value": "1" } }, { "switch": { "cases": [ { "when": "true", "do": [ { "frobnicate": {} } ] } ] } } ]"""));

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe(InterpreterErrorCodes.UnknownNode);
        outcome.Failure.AtStep.Kind.ShouldBe("frobnicate");
        outcome.Stats.Steps.ShouldBe(0); // nothing ran — the leading `set` never executed
    }

    [Fact]
    public async Task Loop_missing_max_iterations_deep_in_a_nested_block_fails_at_parse_with_zero_steps()
    {
        var outcome = await Runner.RunAsync(Payload(
            """[ { "if": { "cond": "true", "then": [ { "loop": { "for": { "var": "i", "from": "0", "to": "1" }, "do": [] } } ] } } ]"""));

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe(InterpreterErrorCodes.MissingMaxIterations);
        outcome.Failure.AtStep.Kind.ShouldBe("loop");
        outcome.Stats.Steps.ShouldBe(0);
    }

    [Fact]
    public async Task ForEach_missing_max_iterations_fails_at_parse()
    {
        var outcome = await Runner.RunAsync(Payload(
            """[ { "forEach": { "in": "['a']", "as": "x", "do": [] } } ]"""));

        outcome.Status.ShouldBe(RunStatus.Failed);
        outcome.Failure!.Code.ShouldBe(InterpreterErrorCodes.MissingMaxIterations);
        outcome.Failure.AtStep.Kind.ShouldBe("forEach");
    }
}
