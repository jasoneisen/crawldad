using System.Text.Json;
using Crawldad.Web.Features.Payloads;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit;

/// <summary>The secretRef validation surface: a <c>secretRef</c> input is a recognised type whose value is a reference
/// only, consumed <b>exclusively</b> by <c>fill.secret</c>. The JSON Schema fixes the <c>fill</c> shape (<c>value</c> XOR
/// <c>secret</c>); the semantic walker rejects a secretRef named anywhere in the expression value space, so a secret can never be interpolated, logged, pushed, or shaped into a result.</summary>
public class SecretRefValidationTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // A payload declaring a `backend` input (for config.backend) plus the given inputs, steps, and result.
    private static JsonElement Payload(string inputs, string steps, string result = "null") =>
        Parse($$"""
            { "crawldad": "1", "name": "t", "config": { "backend": "input.backend" },
              "inputs": { "backend": { "type": "backend" }, {{inputs}} },
              "steps": {{steps}}, "result": "{{result}}" }
            """);

    // The full save-time pipeline (schema first, short-circuit, then the semantic walker) — exactly the endpoint order.
    private static IReadOnlyList<string> ValidateAll(JsonElement payload)
    {
        var schema = PayloadSchema.Validate(payload);
        return schema.Count > 0
            ? [.. schema.Select(e => $"{e.Path}: {e.Code}")]
            : [.. PayloadValidator.Validate(payload).Select(i => $"{i.Path}: {i.Code}")];
    }

    // The shared secretRef-name reader (used by both the walker and the interpreter): only secretRef-typed declarations
    // count, and every degenerate shape (no inputs block, a non-object inputs, an untyped or non-string-typed declaration)
    // yields nothing rather than throwing — the run-time path sees unchecked inline payloads, so this must be total.
    [Fact]
    public void SecretRefInputs_reads_only_secretRef_typed_declarations()
    {
        SecretRefInputs.Names(Parse("""{ "steps": [] }""")).ShouldBeEmpty();                    // no inputs block
        SecretRefInputs.Names(Parse("""{ "inputs": "nope", "steps": [] }""")).ShouldBeEmpty();  // inputs is not an object

        SecretRefInputs.Names(Parse("""
            { "inputs": { "pw": { "type": "secretRef" }, "term": { "type": "string" },
                          "untyped": { }, "weird": { "type": 5 } } }
            """)).ShouldBe(["pw"]); // only the secretRef; an untyped or non-string-typed declaration is ignored
    }

    // ----- secretRef is a recognised input type; consumed only by fill.secret -----

    [Fact]
    public void A_secretRef_input_consumed_only_by_fill_secret_validates_clean()
    {
        var payload = Payload(
            """ "loginPw": { "type": "secretRef" } """,
            """[ { "goto": { "url": "https://example.test/login" } }, { "fill": { "selector": "#password", "secret": "input.loginPw" } } ]""");

        ValidateAll(payload).ShouldBeEmpty();
    }

    [Fact]
    public void An_ordinary_input_referenced_in_an_expression_is_not_flagged()
    {
        // A non-secretRef input used in an expression is fine — only secretRef-typed values are restricted.
        var payload = Payload(
            """ "term": { "type": "string" } """,
            """[ { "set": { "var": "x", "value": "input.term" } } ]""",
            "x");

        ValidateAll(payload).ShouldBeEmpty();
    }

    // ----- a secretRef must not enter the expression value space -----

    [Theory]
    [InlineData("""[ { "set": { "var": "leaked", "value": "input.loginPw" } } ]""", "/steps/0/set/value")]
    [InlineData("""[ { "log": { "level": "info", "message": "pw=${input.loginPw}" } } ]""", "/steps/0/log/message")]
    [InlineData("""[ { "set": { "var": "x", "value": "input['loginPw']" } } ]""", "/steps/0/set/value")]
    [InlineData("""[ { "if": { "cond": "input.loginPw == 'x'", "then": [] } } ]""", "/steps/0/if/cond")]
    public void A_secretRef_used_in_an_expression_is_rejected(string steps, string expectedPath)
    {
        var payload = Payload(""" "loginPw": { "type": "secretRef" } """, steps);

        PayloadValidator.Validate(payload)
            .ShouldContain(i => i.Code == InterpreterErrorCodes.SecretRefInExpression && i.Path == expectedPath);
    }

    [Fact]
    public void A_secretRef_shaped_into_the_result_is_rejected()
    {
        var payload = Payload(
            """ "loginPw": { "type": "secretRef" } """,
            """[ ]""",
            "input.loginPw");

        PayloadValidator.Validate(payload)
            .ShouldContain(i => i.Code == InterpreterErrorCodes.SecretRefInExpression && i.Path == "/result");
    }

    [Fact]
    public void A_fill_secret_referencing_a_non_secretRef_input_is_rejected()
    {
        var payload = Payload(
            """ "url": { "type": "string" } """,
            """[ { "fill": { "selector": "#password", "secret": "input.url" } } ]""");

        PayloadValidator.Validate(payload)
            .ShouldContain(i => i.Code == InterpreterErrorCodes.FillSecretNotSecretRef && i.Path == "/steps/0/fill/secret");
    }

    [Fact]
    public void A_fill_secret_that_is_not_a_bare_input_reference_is_rejected()
    {
        // fill.secret is a restricted reference, never a general expression — a computed value is rejected.
        var payload = Payload(
            """ "loginPw": { "type": "secretRef" } """,
            """[ { "fill": { "selector": "#password", "secret": "input.loginPw + 'x'" } } ]""");

        PayloadValidator.Validate(payload)
            .ShouldContain(i => i.Code == InterpreterErrorCodes.FillSecretNotSecretRef);
    }

    [Fact]
    public void A_fill_secret_reference_may_use_the_index_form()
    {
        var payload = Payload(
            """ "loginPw": { "type": "secretRef" } """,
            """[ { "fill": { "selector": "#password", "secret": "input['loginPw']" } } ]""");

        ValidateAll(payload).ShouldBeEmpty();
    }

    [Fact]
    public void A_fill_secret_that_fails_to_parse_surfaces_the_parse_error()
    {
        // The schema accepts any string for `secret`; a malformed reference surfaces the parser's own code at the secret path.
        var payload = Payload(
            """ "loginPw": { "type": "secretRef" } """,
            """[ { "fill": { "selector": "#password", "secret": "1 +" } } ]""");

        PayloadValidator.Validate(payload)
            .ShouldContain(i => i.Code == ExpressionErrorCodes.SyntaxError && i.Path == "/steps/0/fill/secret");
    }

    // ----- schema: fill takes a value XOR a secret -----

    [Fact]
    public void The_schema_accepts_a_secretRef_input_type() =>
        PayloadSchema.Validate(Payload(
            """ "loginPw": { "type": "secretRef" } """,
            """[ { "fill": { "selector": "#p", "secret": "input.loginPw" } } ]""")).ShouldBeEmpty();

    [Fact]
    public void The_schema_rejects_a_fill_with_both_value_and_secret() =>
        PayloadSchema.Validate(Payload(
            """ "loginPw": { "type": "secretRef" } """,
            """[ { "fill": { "selector": "#p", "value": "'a'", "secret": "input.loginPw" } } ]""")).ShouldNotBeEmpty();

    [Fact]
    public void The_schema_rejects_a_fill_with_neither_value_nor_secret() =>
        PayloadSchema.Validate(Payload(
            """ "loginPw": { "type": "secretRef" } """,
            """[ { "fill": { "selector": "#p" } } ]""")).ShouldNotBeEmpty();

    [Fact]
    public void The_schema_still_accepts_a_plain_value_fill() =>
        PayloadSchema.Validate(Payload(
            """ "term": { "type": "string" } """,
            """[ { "fill": { "selector": "#p", "value": "input.term" } } ]""")).ShouldBeEmpty();
}
