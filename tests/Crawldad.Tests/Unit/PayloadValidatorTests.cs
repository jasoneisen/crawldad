using System.Text.Json;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Payloads;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit;

/// <summary>
/// White-box tests for the shared save-time validator (Deliverable 3): the JSON Schema pass + the semantic pass over
/// the two canonical payloads (B.1/B.2 must validate clean), the rarer valid shapes, and the semantic reject cases.
/// The run-time pre-pass reuses the same <see cref="PayloadValidator.ValidateStructure"/>, exercised by
/// <see cref="PayloadValidationTests"/>.
/// </summary>
public class PayloadValidatorTests
{
    private static JsonElement Load(string fixture)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Runner.FixturesRoot, "Payloads", fixture)));
        return doc.RootElement.Clone();
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // The full save-time pipeline: schema first (short-circuit), then the semantic pass — mirroring the endpoint.
    private static IReadOnlyList<string> ValidateAll(JsonElement payload)
    {
        var schema = PayloadSchema.Validate(payload);
        if (schema.Count > 0)
        {
            return [.. schema.Select(e => $"{e.Path}: {e.Code}: {e.Message}")];
        }

        return [.. PayloadValidator.Validate(payload).Select(i => $"{i.Path}: {i.Code}: {i.Message}")];
    }

    private static IReadOnlyList<PayloadIssue> Semantic(string json) => PayloadValidator.Validate(Parse(json));

    // ----- accept: the canonical payloads validate clean --------------------------------------------------------

    [Fact]
    public void Search_payload_B1_validates_clean() => ValidateAll(Load("search-full.json")).ShouldBeEmpty();

    [Fact]
    public void Scrape_payload_B2_validates_clean() => ValidateAll(Load("scrape-full.json")).ShouldBeEmpty();

    // A valid payload exercising the rarer shapes B.1/B.2 do not: no `vars` block; a `download` idempotencyKey; a
    // `locate` from-form with a filter and no nth; a `for` loop with a step; a `forEach` with an index; and a
    // structured selector map carrying base/css/in/nth/first.
    [Fact]
    public void Rare_but_valid_shapes_validate_clean()
    {
        const string Payload =
            """
            { "crawldad": "1", "name": "kitchen", "config": { "backend": "input.backend" },
              "steps": [
                { "frame": { "var": "fr", "selector": "#f" } },
                { "locate": { "var": "rows", "selector": "tr" } },
                { "locate": { "var": "voids", "from": "rows", "filter": { "hasTextRegex": "x" } } },
                { "loop": { "maxIterations": 10, "for": { "var": "i", "from": "0", "to": "5", "step": "1" }, "do": [
                    { "waitFor": { "selector": { "base": "rows", "css": "td", "in": "fr", "nth": "0", "first": true } } }
                ] } },
                { "forEach": { "in": "['a']", "as": "x", "index": "ix", "maxIterations": 10, "do": [
                    { "download": { "trigger": [ { "click": { "selector": "a" } } ], "to": "input.store", "idempotencyKey": "x", "var": "dl" } }
                ] } }
              ],
              "result": "null" }
            """;
        ValidateAll(Parse(Payload)).ShouldBeEmpty();
    }

    // A loop variable that shadows an outer loop variable of the same name is valid — it is bound for the inner body
    // and the outer binding is restored on exit (§8.2). Exercises the walker's shadow (add-nothing / remove-nothing) path.
    [Fact]
    public void A_shadowing_loop_variable_validates_clean()
    {
        const string Payload =
            """
            { "crawldad": "1", "name": "shadow", "config": { "backend": "input.backend" }, "vars": { "acc": [] },
              "steps": [
                { "forEach": { "in": "['a']", "as": "x", "maxIterations": 5, "do": [
                    { "forEach": { "in": "['b']", "as": "x", "maxIterations": 5, "do": [
                        { "push": { "into": "acc", "value": "x" } }
                    ] } }
                ] } }
              ],
              "result": "acc" }
            """;
        ValidateAll(Parse(Payload)).ShouldBeEmpty();
    }

    // #8: a screenshot node with no body (the name-absent walker branch) and one whose name Tmpl interpolates an input
    // (the name-present branch) both validate clean — input sub-keys are not resolved as references (§12 walker leniency).
    [Fact]
    public void Screenshot_nodes_with_and_without_a_name_validate_clean() =>
        ValidateAll(Parse(Steps("""[ { "screenshot": {} }, { "screenshot": { "name": "shot-${input.tag}" } } ]"""))).ShouldBeEmpty();

    private static string Steps(string steps) =>
        $$"""{ "crawldad": "1", "name": "t", "config": { "backend": "input.backend" }, "vars": {}, "steps": {{steps}}, "result": "null" }""";

    // ----- reject: the semantic pass catches what the schema cannot -----------------------------------------------

    [Fact]
    public void Undefined_variable_use_is_rejected()
    {
        var issues = Semantic(Steps("""[ { "set": { "var": "x", "value": "undefinedThing + 1" } } ]"""));
        issues.ShouldHaveSingleItem().Code.ShouldBe(InterpreterErrorCodes.UndefinedReference);
        issues[0].Path.ShouldBe("/steps/0/set/value");
    }

    [Fact]
    public void An_undefined_push_target_is_rejected()
    {
        var issues = Semantic(Steps("""[ { "push": { "into": "notDefined", "value": "1" } } ]"""));
        issues.ShouldContain(i => i.Code == InterpreterErrorCodes.UndefinedReference && i.Path == "/steps/0/push/into");
    }

    [Fact]
    public void An_unknown_builtin_is_rejected()
    {
        Semantic(Steps("""[ { "set": { "var": "x", "value": "bogusFn(1)" } } ]"""))
            .ShouldHaveSingleItem().Code.ShouldBe(ExpressionErrorCodes.UnknownFunction);
    }

    [Fact]
    public void A_wrong_arity_call_is_rejected()
    {
        Semantic(Steps("""[ { "set": { "var": "x", "value": "trim()" } } ]"""))
            .ShouldHaveSingleItem().Code.ShouldBe(ExpressionErrorCodes.WrongArity);
    }

    [Fact]
    public void A_syntax_error_is_rejected()
    {
        Semantic(Steps("""[ { "set": { "var": "x", "value": "1 +" } } ]"""))
            .ShouldHaveSingleItem().Code.ShouldBe(ExpressionErrorCodes.SyntaxError);
    }

    [Fact]
    public void A_bad_interpolation_in_a_template_field_is_rejected()
    {
        var issues = Semantic(Steps("""[ { "log": { "level": "info", "message": "x${1 +}y" } } ]"""));
        issues.ShouldHaveSingleItem().Code.ShouldBe(ExpressionErrorCodes.SyntaxError);
        issues[0].Path.ShouldBe("/steps/0/log/message");
    }

    [Fact]
    public void A_screenshot_name_referencing_an_undefined_var_is_rejected() // #8: the name Tmpl is checked like any sibling template field
    {
        var issues = Semantic(Steps("""[ { "screenshot": { "name": "shot-${nope}" } } ]"""));
        issues.ShouldHaveSingleItem().Code.ShouldBe(InterpreterErrorCodes.UndefinedReference);
        issues[0].Path.ShouldBe("/steps/0/screenshot/name");
    }

    [Fact]
    public void A_bad_interpolation_in_a_set_path_is_rejected()
    {
        Semantic(Steps("""[ { "set": { "var": "m", "value": "{}", "path": "[${1 +}]" } } ]"""))
            .ShouldHaveSingleItem().Code.ShouldBe(ExpressionErrorCodes.SyntaxError);
    }

    [Fact]
    public void An_unterminated_set_path_bracket_is_rejected()
    {
        Semantic(Steps("""[ { "set": { "var": "m", "value": "{}", "path": "[oops" } } ]"""))
            .ShouldHaveSingleItem().Code.ShouldBe(InterpreterErrorCodes.MalformedNode);
    }
}
