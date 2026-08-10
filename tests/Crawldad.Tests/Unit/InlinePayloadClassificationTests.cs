using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs.Interpreter;

namespace Crawldad.Tests.Unit;

/// <summary>Issue #48: an inline <c>POST /runs</c> payload is NOT schema-validated (only the structural pre-pass runs),
/// so a node/config/top-level field of the wrong JSON kind — or a missing required one — must terminate as a classified
/// terminal <c>malformed_node</c> run failure, never a raw JsonElement-accessor throw (an unhandled 500). Each case runs
/// the malformed payload against the fake backend: that the harness returns a Failed outcome at all — rather than the
/// <c>await</c> throwing the uncaught accessor exception — is itself the "not a 500" proof.</summary>
public class InlinePayloadClassificationTests
{
    private static string WithSteps(string steps) =>
        $$"""{ "name": "t", "config": { "backend": "input.backend" }, "vars": {}, "steps": {{steps}}, "result": "null" }""";

    private static async Task<string> FailureCode(string payload)
    {
        var outcome = await Runner.RunAsync(payload);
        outcome.Status.ShouldBe(RunStatus.Failed, outcome.Failure?.Code);
        return outcome.Failure!.Code;
    }

    // Every node executor field: a wrong JSON kind, or a missing required field, classifies as malformed_node instead
    // of throwing a raw GetString/GetBoolean/GetInt/EnumerateArray on the unvalidated inline JSON.
    [Theory]
    [InlineData("""[ { "goto": { "url": 5 } } ]""")]
    [InlineData("""[ { "goto": {} } ]""")]
    [InlineData("""[ { "waitForLoadState": { "state": 5 } } ]""")]
    [InlineData("""[ { "waitFor": { "selector": 5 } } ]""")]
    [InlineData("""[ { "waitFor": {} } ]""")]
    [InlineData("""[ { "click": {} } ]""")]
    [InlineData("""[ { "clear": {} } ]""")]
    [InlineData("""[ { "frame": { "var": 5, "selector": "#f" } } ]""")]
    [InlineData("""[ { "frame": { "var": "f", "selector": 5 } } ]""")]
    [InlineData("""[ { "addStyleTag": { "content": 5 } } ]""")]
    [InlineData("""[ { "waitForRequest": { "urlPrefix": 5 } } ]""")]
    [InlineData("""[ { "waitForRequest": { "urlPrefix": "https://x" } } ]""")]         // missing trigger
    [InlineData("""[ { "fill": { "selector": "input", "value": 5 } } ]""")]
    [InlineData("""[ { "fill": { "selector": "input", "secret": 5 } } ]""")]
    [InlineData("""[ { "locate": { "var": 5, "selector": "tr" } } ]""")]
    [InlineData("""[ { "locate": { "var": "x" } } ]""")]                                // missing selector
    [InlineData("""[ { "locate": { "var": "x", "from": 5 } } ]""")]
    [InlineData("""[ { "locate": { "var": "x", "base": 5, "selector": "td" } } ]""")]
    [InlineData("""[ { "download": { "var": "d", "to": 5 } } ]""")]
    [InlineData("""[ { "download": { "var": "d", "to": "{ 'kind': 'fake' }" } } ]""")]  // missing trigger
    [InlineData("""[ { "set": { "var": 5, "value": "1" } } ]""")]
    [InlineData("""[ { "set": { "var": "x", "value": 5 } } ]""")]
    [InlineData("""[ { "set": { "var": "m", "value": "{}", "path": 5 } } ]""")]
    [InlineData("""[ { "push": { "into": 5, "value": "1" } } ]""")]
    [InlineData("""[ { "push": { "into": "acc", "value": 5 } } ]""")]
    [InlineData("""[ { "log": { "level": 5, "message": "hi" } } ]""")]
    [InlineData("""[ { "log": { "level": "info", "message": 5 } } ]""")]
    [InlineData("""[ { "guard": { "cond": 5 } } ]""")]
    [InlineData("""[ { "guard": { "cond": "false", "elseFail": 5 } } ]""")]
    [InlineData("""[ { "fail": { "class": 5, "code": "c", "message": "m" } } ]""")]
    [InlineData("""[ { "if": { "cond": 5, "then": [] } } ]""")]
    [InlineData("""[ { "if": { "cond": "true" } } ]""")]                                // missing then (cond true)
    [InlineData("""[ { "switch": {} } ]""")]                                            // missing cases
    [InlineData("""[ { "switch": { "cases": [ { "when": 5, "do": [] } ] } } ]""")]
    [InlineData("""[ { "switch": { "cases": [ { "when": "true" } ] } } ]""")]           // missing case do
    [InlineData("""[ { "loop": { "maxIterations": 2.5, "for": { "var": "i", "from": 0, "to": 1 }, "do": [] } } ]""")]
    [InlineData("""[ { "loop": { "maxIterations": 5, "do": [] } } ]""")]                // missing for (and no while)
    [InlineData("""[ { "loop": { "maxIterations": 5, "for": { "var": 5, "from": 0, "to": 1 }, "do": [] } } ]""")]
    [InlineData("""[ { "loop": { "maxIterations": 5, "for": { "var": "i", "from": 0, "to": 1 } } } ]""")] // missing do
    [InlineData("""[ { "loop": { "maxIterations": 5, "for": { "var": "i", "from": 0, "to": 1, "inclusiveTo": "yes" }, "do": [] } } ]""")]
    [InlineData("""[ { "loop": { "maxIterations": 5, "for": { "var": "i", "from": 0 }, "do": [] } } ]""")] // missing to
    [InlineData("""[ { "loop": { "maxIterations": 5, "while": "false" } } ]""")]        // missing do (while form)
    [InlineData("""[ { "forEach": { "maxIterations": 5, "in": "[]", "as": 5, "do": [] } } ]""")]
    [InlineData("""[ { "forEach": { "maxIterations": 5, "in": 5, "as": "x", "do": [] } } ]""")]
    [InlineData("""[ { "forEach": { "maxIterations": 5, "in": "[]", "as": "x" } } ]""")] // missing do
    [InlineData("""[ { "forEach": { "maxIterations": 5, "in": "[]", "as": "x", "index": 5, "do": [] } } ]""")]
    [InlineData("""[ { "break": { "when": 5 } } ]""")]
    [InlineData("""[ { "continue": { "when": 5 } } ]""")]
    public async Task Malformed_inline_node_field_is_a_terminal_malformed_node(string steps) =>
        (await FailureCode(WithSteps(steps))).ShouldBe(InterpreterErrorCodes.MalformedNode);

    // The structured-Sel refinements on a bound handle (locate.from) each read an uncoerced field; a wrong kind there is
    // malformed_node, reached only after the handle is bound, so each case first binds `rows`.
    [Theory]
    [InlineData(""" { "locate": { "var": "x", "from": "rows", "first": "yes" } } """)]
    [InlineData(""" { "locate": { "var": "x", "from": "rows", "nth": 5 } } """)]
    [InlineData(""" { "locate": { "var": "x", "from": "rows", "filter": 5 } } """)]
    public async Task Malformed_locate_from_refinement_is_a_terminal_malformed_node(string secondStep) =>
        (await FailureCode(WithSteps($$"""[ { "locate": { "var": "rows", "selector": "tr" } }, {{secondStep}} ]""")))
            .ShouldBe(InterpreterErrorCodes.MalformedNode);

    // The structural pre-pass runs on the raw inline JSON: a wrong-kinded top-level field, step, node body, block, or
    // switch case classifies as malformed_node rather than throwing a raw EnumerateArray/EnumerateObject/First.
    [Theory]
    [InlineData("""{ "config": { "backend": "input.backend" }, "vars": {}, "steps": 5, "result": "null" }""")]
    [InlineData("""{ "config": { "backend": "input.backend" }, "vars": {}, "steps": [ 5 ], "result": "null" }""")]
    [InlineData("""{ "config": { "backend": "input.backend" }, "vars": {}, "steps": [ {} ], "result": "null" }""")]
    [InlineData("""{ "config": { "backend": "input.backend" }, "vars": {}, "steps": [ { "goto": 5 } ], "result": "null" }""")]
    [InlineData("""{ "config": { "backend": "input.backend" }, "vars": {}, "steps": [ { "if": { "cond": "true", "then": 5 } } ], "result": "null" }""")]
    [InlineData("""{ "config": { "backend": "input.backend" }, "vars": {}, "steps": [ { "switch": { "cases": 5 } } ], "result": "null" }""")]
    [InlineData("""{ "config": { "backend": "input.backend" }, "vars": {}, "steps": [ { "switch": { "cases": [ 5 ] } } ], "result": "null" }""")]
    [InlineData("""{ "config": { "backend": "input.backend" }, "vars": {}, "result": "null" }""")] // steps missing
    [InlineData("""{ "vars": {}, "steps": [], "result": "null" }""")]                    // config missing
    [InlineData("""{ "config": 5, "steps": [], "result": "null" }""")]                    // config not an object
    [InlineData("""{ "config": { "backend": "input.backend" }, "steps": [] }""")]         // result missing
    [InlineData("""{ "config": { "backend": "input.backend" }, "steps": [], "result": 5 }""")] // result not a string
    [InlineData("""{ "config": { "backend": "input.backend" }, "vars": 5, "steps": [], "result": "null" }""")] // vars not an object
    public async Task Malformed_inline_structure_is_a_terminal_malformed_node(string payload) =>
        (await FailureCode(payload)).ShouldBe(InterpreterErrorCodes.MalformedNode);

    // Every config sub-field the interpreter reads during setup: a wrong kind classifies as malformed_node instead of a
    // raw GetInt32/GetBoolean/EnumerateArray on the unvalidated inline config.
    [Theory]
    [InlineData("""{ "backend": 5 }""")]
    [InlineData("""{ "backend": "input.backend", "screenshotOnFailure": "yes" }""")]
    [InlineData("""{ "backend": "input.backend", "defaultTimeoutMs": "x" }""")]
    [InlineData("""{ "backend": "input.backend", "launch": 5 }""")]
    [InlineData("""{ "backend": "input.backend", "launch": { "args": 5 } }""")]
    [InlineData("""{ "backend": "input.backend", "launch": { "args": [ 5 ] } }""")]
    [InlineData("""{ "backend": "input.backend", "context": 5 }""")]
    [InlineData("""{ "backend": "input.backend", "context": { "bypassCsp": "yes" } }""")]
    [InlineData("""{ "backend": "input.backend", "route": 5 }""")]
    [InlineData("""{ "backend": "input.backend", "route": { "throttle": 5 } }""")]
    [InlineData("""{ "backend": "input.backend", "route": { "throttle": { "minIntervalMs": "x" } } }""")]
    [InlineData("""{ "backend": "input.backend", "route": { "blockHosts": 5 } }""")]
    [InlineData("""{ "backend": "input.backend", "route": { "cacheUrlSuffixes": 5 } }""")]
    [InlineData("""{ "backend": "input.backend", "retry": 5 }""")]
    [InlineData("""{ "backend": "input.backend", "retry": { "maxAttempts": "x" } }""")]
    [InlineData("""{ "backend": "input.backend", "retry": { "delayMs": "x" } }""")]
    [InlineData("""{ "backend": "input.backend", "retry": { "retryOn": 5 } }""")]
    [InlineData("""{ "backend": "input.backend", "retry": { "retryOn": [ 5 ] } }""")]
    public async Task Malformed_inline_config_field_is_a_terminal_malformed_node(string config) =>
        (await FailureCode($$"""{ "name": "t", "config": {{config}}, "vars": {}, "steps": [], "result": "'ok'" }"""))
            .ShouldBe(InterpreterErrorCodes.MalformedNode);

    // The two node fields read only on the durable (observer) path — a screenshot label and a checkpoint's name — are
    // classified there too; the synchronous path never reads them (screenshot/checkpoint no-op without an observer).
    [Theory]
    [InlineData("""[ { "screenshot": { "name": 5 } } ]""")]
    [InlineData("""[ { "checkpoint": { "name": 5, "cursor": "1" } } ]""")]
    public async Task Malformed_durable_only_node_field_is_a_terminal_malformed_node(string steps)
    {
        var (outcome, _, _) = await Runner.RunWithObserverAsync(WithSteps(steps));
        outcome.Status.ShouldBe(RunStatus.Failed, outcome.Failure?.Code);
        outcome.Failure!.Code.ShouldBe(InterpreterErrorCodes.MalformedNode);
    }
}
