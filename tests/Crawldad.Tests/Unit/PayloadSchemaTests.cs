using System.Text.Json;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Payloads;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The JSON Schema gate (Deliverable 1): <c>schema/crawldad-1.schema.json</c> accepts the canonical payloads and
/// rejects the structural violations the semantic pass is not meant to catch — an unknown node head, a loop missing
/// its <c>maxIterations</c> cap, a bad enum value, a mistyped field, a missing required field, and an unknown
/// top-level key. Errors carry a JSON-Pointer path into the document.
/// </summary>
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

    [Theory]
    [InlineData("search-full.json")]
    [InlineData("scrape-full.json")]
    public void The_canonical_payloads_satisfy_the_schema(string fixture) =>
        PayloadSchema.Validate(Load(fixture)).ShouldBeEmpty();

    [Theory]
    [InlineData("""[ { "screenshot": {} } ]""")]                        // #8: an empty (all-optional) body is valid
    [InlineData("""[ { "screenshot": { "name": "after-login" } } ]""")] // with the optional author label
    public void A_screenshot_node_satisfies_the_schema(string steps) =>
        PayloadSchema.Validate(Parse(Steps(steps))).ShouldBeEmpty();

    [Fact]
    public void A_screenshot_node_with_an_unknown_property_fails_the_schema() => // #8: additionalProperties:false — no to/selector/fullPage yet
        PayloadSchema.Validate(Parse(Steps("""[ { "screenshot": { "selector": "#x" } } ]"""))).ShouldNotBeEmpty();

    [Fact]
    public void A_download_node_carrying_the_removed_idempotency_key_fails_the_schema()
    {
        // CD-11: download.idempotencyKey was accepted-but-ignored, then removed. The download node is
        // additionalProperties:false, so the field is now rejected at save time exactly like any unknown property —
        // content addressing already provides the stored:true dedup (§9.3). The reject descends into the download node.
        var errors = PayloadSchema.Validate(Parse(Steps(
            """[ { "download": { "trigger": [ { "click": { "selector": "a" } } ], "to": "input.store", "var": "dl", "idempotencyKey": "x" } } ]""")));
        errors.ShouldContain(e => e.Path.StartsWith("/steps/0/download", StringComparison.Ordinal));
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
    // CD-10: loop.for from/to/step accept a typed JSON number as well as an Expr string. All-typed, a computed Expr `to`
    // mixed with typed from/step, and a negative typed step all satisfy the number|string union.
    [InlineData("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": 5, "step": 2 }, "do": [] } } ]""")]
    [InlineData("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 0, "to": "count(rows)", "step": 1 }, "do": [] } } ]""")]
    [InlineData("""[ { "loop": { "maxIterations": 10, "for": { "var": "i", "from": 5, "to": 0, "step": -1 }, "do": [] } } ]""")]
    public void A_loop_with_typed_numeric_bounds_satisfies_the_schema(string steps) =>
        PayloadSchema.Validate(Parse(Steps(steps))).ShouldBeEmpty();

    [Fact]
    public void A_loop_bound_that_is_neither_a_number_nor_a_string_fails_the_schema() // CD-10: the number|string union rejects a boolean step
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
