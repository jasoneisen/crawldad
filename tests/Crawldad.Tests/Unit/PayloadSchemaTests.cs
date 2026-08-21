using System.Text.Json;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Payloads;

namespace Crawldad.Tests.Unit;

/// <summary>The JSON Schema gate: <c>schema/crawldad-1.schema.json</c> accepts the canonical payloads and rejects
/// the structural violations the semantic pass is not meant to catch — an unknown node head, a missing
/// <c>maxIterations</c> cap, a bad enum/type, a missing field, an unknown key — errors carrying a JSON-Pointer path.</summary>
public class PayloadSchemaTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement Load(string fixture)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Runner.FixturesRoot, "Payloads", fixture)));
        return doc.RootElement.Clone();
    }

    private static string Steps(string steps) =>
        $$"""{ "crawldad": "1", "name": "t", "config": { "backend": "input.backend" }, "vars": {}, "steps": {{steps}}, "result": "null" }""";

    private static string WithRetry(string retry) =>
        $$"""{ "crawldad": "1", "name": "t", "config": { "backend": "input.backend", "retry": {{retry}} }, "steps": [], "result": "null" }""";

    [Theory]
    [InlineData("search-full.json")]
    [InlineData("scrape-full.json")]
    public void The_canonical_payloads_satisfy_the_schema(string fixture) =>
        PayloadSchema.Validate(Load(fixture)).ShouldBeEmpty();

    [Theory]
    [InlineData("""[ { "screenshot": {} } ]""")]                        // an empty (all-optional) body is valid
    [InlineData("""[ { "screenshot": { "name": "after-login" } } ]""")] // with the optional author label
    public void A_screenshot_node_satisfies_the_schema(string steps) =>
        PayloadSchema.Validate(Parse(Steps(steps))).ShouldBeEmpty();

    [Fact]
    public void A_screenshot_node_with_an_unknown_property_fails_the_schema() => // additionalProperties:false — no to/selector/fullPage yet
        PayloadSchema.Validate(Parse(Steps("""[ { "screenshot": { "selector": "#x" } } ]"""))).ShouldNotBeEmpty();

    // The structured-Sel role/text/xpath variants, each as a lone root, role with an accessible-name `name`, and
    // the base+css pair (the sole two-root combination) satisfy the schema.
    [Theory]
    [InlineData("""[ { "click": { "selector": { "role": "button", "name": "Go" } } } ]""")]
    [InlineData("""[ { "click": { "selector": { "role": "textbox" } } } ]""")]
    [InlineData("""[ { "click": { "selector": { "role": "switch" } } } ]""")] // the fuller ARIA vocabulary, not just the common few
    [InlineData("""[ { "click": { "selector": { "text": "Open" } } } ]""")]
    [InlineData("""[ { "click": { "selector": { "xpath": "//button" } } } ]""")]
    [InlineData("""[ { "waitFor": { "selector": { "base": "row", "css": "td" } } } ]""")]
    public void A_structured_selector_variant_satisfies_the_schema(string steps) =>
        PayloadSchema.Validate(Parse(Steps(steps))).ShouldBeEmpty();

    // Exactly one root, only base+css may combine, and `name` requires `role` — the schema rejects every violation.
    [Theory]
    [InlineData("""[ { "click": { "selector": { "css": "#a", "title": "b" } } } ]""")]     // two page/frame roots
    [InlineData("""[ { "click": { "selector": { "role": "button", "text": "x" } } } ]""")]  // two page/frame roots
    [InlineData("""[ { "click": { "selector": { "base": "row", "title": "x" } } } ]""")]    // base + a non-css root
    [InlineData("""[ { "click": { "selector": { "css": "#a", "name": "x" } } } ]""")]       // name without role
    [InlineData("""[ { "click": { "selector": { "nth": "0" } } } ]""")]                      // no root at all
    [InlineData("""[ { "click": { "selector": { "role": "button", "bogus": "x" } } } ]""")]  // unknown key
    [InlineData("""[ { "click": { "selector": { "role": "notarole" } } } ]""")]              // role outside the ARIA vocabulary
    public void An_invalid_structured_selector_fails_the_schema(string steps) =>
        PayloadSchema.Validate(Parse(Steps(steps))).ShouldNotBeEmpty();

    [Fact]
    public void A_download_node_carrying_the_removed_idempotency_key_fails_the_schema()
    {
        // download.idempotencyKey was accepted-but-ignored, then removed: the download node is additionalProperties:false,
        // so the field is now rejected at save time exactly like any unknown property — content addressing already
        // provides the stored:true dedup. The reject descends into the download node.
        var errors = PayloadSchema.Validate(Parse(Steps(
            """[ { "download": { "trigger": [ { "click": { "selector": "a" } } ], "to": "input.store", "var": "dl", "idempotencyKey": "x" } } ]""")));
        errors.ShouldContain(e => e.Path.StartsWith("/steps/0/download", StringComparison.Ordinal));
    }

    // A `capture` accepts the full-document (no selector), element-subtree (selector), and subtree-in-a-frame
    // (selector + in) shapes — `in` is optional but, when present, coexists with a selector.
    [Theory]
    [InlineData("""[ { "capture": { "to": "input.store", "var": "c" } } ]""")]
    [InlineData("""[ { "capture": { "to": "input.store", "selector": "#g", "var": "c" } } ]""")]
    [InlineData("""[ { "capture": { "to": "input.store", "selector": "#g", "in": "fr", "var": "c" } } ]""")]
    public void A_capture_node_satisfies_the_schema(string steps) =>
        PayloadSchema.Validate(Parse(Steps(steps))).ShouldBeEmpty();

    [Fact]
    public void A_capture_with_in_but_no_selector_fails_the_schema() // dependentRequired: `in` scopes a selector, so it requires one
    {
        var errors = PayloadSchema.Validate(Parse(Steps("""[ { "capture": { "to": "input.store", "in": "fr", "var": "c" } } ]""")));
        errors.ShouldContain(e => e.Path.StartsWith("/steps/0/capture", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_node_head_fails_the_schema()
    {
        var errors = PayloadSchema.Validate(Parse(Steps("""[ { "frobnicate": {} } ]""")));
        errors.ShouldNotBeEmpty();
        errors.ShouldContain(e => e.Path.StartsWith("/steps/0", StringComparison.Ordinal));
    }

    [Fact]
    public void A_loop_missing_max_iterations_fails_the_schema()
    {
        var errors = PayloadSchema.Validate(Parse(Steps("""[ { "loop": { "for": { "var": "i", "from": "0", "to": "1" }, "do": [] } } ]""")));
        errors.ShouldNotBeEmpty();
        errors.ShouldContain(e => e.Path.StartsWith("/steps/0", StringComparison.Ordinal));
    }

    [Theory]
    // loop.for from/to/step accept a typed JSON number as well as an Expr string. All-typed, a computed Expr `to`
    // mixed with typed from/step, and a negative typed step all satisfy the number|string union.
    [InlineData("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": 5, "step": 2 }, "do": [] } } ]""")]
    [InlineData("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": "count(rows)", "step": 1 }, "do": [] } } ]""")]
    [InlineData("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 5, "to": 0, "step": -1 }, "do": [] } } ]""")]
    public void A_loop_with_typed_numeric_bounds_satisfies_the_schema(string steps) =>
        PayloadSchema.Validate(Parse(Steps(steps))).ShouldBeEmpty();

    [Fact]
    public void A_loop_bound_that_is_neither_a_number_nor_a_string_fails_the_schema() // the number|string union rejects a boolean step
    {
        var errors = PayloadSchema.Validate(Parse(Steps("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": 5, "step": true }, "do": [] } } ]""")));
        errors.ShouldNotBeEmpty();
        errors.ShouldContain(e => e.Path.StartsWith("/steps/0", StringComparison.Ordinal));
    }

    [Fact]
    public void A_forEach_missing_max_iterations_fails_the_schema() =>
        PayloadSchema.Validate(Parse(Steps("""[ { "forEach": { "in": "['a']", "as": "x", "do": [] } } ]""")))
            .ShouldNotBeEmpty();

    [Fact]
    public void A_bad_enum_value_fails_the_schema() =>
        PayloadSchema.Validate(Parse(Steps("""[ { "log": { "level": "loud", "message": "x" } } ]""")))
            .ShouldNotBeEmpty();

    [Theory]
    // config.retry.backoff is now a tightened enum; the optional maxDelayMs cap and jitter flag ride alongside it, and an
    // omitted backoff still validates (it defaults to constant).
    [InlineData("""{ "maxAttempts": 3, "delayMs": 100, "backoff": "constant" }""")]
    [InlineData("""{ "maxAttempts": 3, "delayMs": 100, "backoff": "linear" }""")]
    [InlineData("""{ "maxAttempts": 3, "delayMs": 100, "backoff": "exponential", "maxDelayMs": 5000 }""")]
    [InlineData("""{ "maxAttempts": 3, "delayMs": 100, "backoff": "exponential", "jitter": true }""")]
    [InlineData("""{ "maxAttempts": 3, "delayMs": 100 }""")]
    public void A_valid_retry_backoff_satisfies_the_schema(string retry) =>
        PayloadSchema.Validate(Parse(WithRetry(retry))).ShouldBeEmpty();

    [Theory]
    [InlineData("""{ "backoff": "fibonacci" }""")]   // outside the shipped enum
    [InlineData("""{ "backoff": "Exponential" }""")] // the tokens are lowercase
    [InlineData("""{ "backoff": 2 }""")]             // not a string
    [InlineData("""{ "maxDelayMs": -1 }""")]         // minimum 0
    [InlineData("""{ "maxDelayMs": 1.5 }""")]        // integer only
    [InlineData("""{ "jitter": "yes" }""")]          // boolean only
    public void An_invalid_retry_backoff_fails_the_schema(string retry) =>
        PayloadSchema.Validate(Parse(WithRetry(retry))).ShouldNotBeEmpty();

    [Fact]
    public void A_two_headed_node_fails_the_schema() =>
        PayloadSchema.Validate(Parse(Steps("""[ { "goto": { "url": "x" }, "click": { "selector": "y" } } ]""")))
            .ShouldNotBeEmpty();

    [Fact]
    public void A_missing_required_top_level_field_fails_the_schema()
    {
        // No `result` — an empty instance-location root error, exercising the root-pointer path.
        var errors = PayloadSchema.Validate(Parse("""{ "crawldad": "1", "name": "t", "config": { "backend": "input.backend" }, "steps": [] }"""));
        errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void An_unknown_top_level_key_fails_the_schema() =>
        PayloadSchema.Validate(Parse("""{ "crawldad": "1", "name": "t", "config": { "backend": "input.backend" }, "steps": [], "result": "null", "bogus": 1 }"""))
            .ShouldNotBeEmpty();
}
