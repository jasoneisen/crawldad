using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Crawldad.Contracts;
using Crawldad.Contracts.Payloads;
using Crawldad.Contracts.Runs;
using Crawldad.Web.Features.Docs;
using Json.Schema;
using Microsoft.AspNetCore.Authorization;
using Wolverine.Http;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The OpenAPI drift gate (#21): keeps <see cref="OpenApiSpec"/> (served at <c>GET /openapi.json</c>) truthful the same way
/// <see cref="DocsDriftTests"/> keeps the reference docs honest. The envelope is authored from a single endpoint table; these
/// tests cross-check that table against the <b>live Wolverine route table</b> (reflected from the
/// <see cref="WolverineHttpMethodAttribute"/>s on the endpoint methods) — so a new routable endpoint added without a spec
/// entry, or with the wrong auth, fails the build. They also assert the document is internally consistent (every
/// <c>$ref</c> resolves, every path parameter is declared, every component schema is a valid JSON Schema) and that the
/// admission surface the ticket calls out — the <c>202</c> queued/running shapes and the <c>429 queue_depth_exceeded</c>
/// limit — is present.
/// </summary>
public class OpenApiSpecTests
{
    private static readonly JsonNode _document = JsonNode.Parse(OpenApiSpec.DocumentJson)!;

    private static readonly Regex _pathParam = new(@"\{(?<name>[^}:]+)(?::[^}]+)?\}", RegexOptions.Compiled | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    // Every routable HTTP endpoint in the app, reflected from the Wolverine method attributes (the source the live route
    // table is built from): the method, the route template, and whether it opts out of the tenant gate with [AllowAnonymous].
    public static IEnumerable<object[]> RoutableEndpoints() =>
        typeof(OpenApiSpec).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(method => (type, method, attr: method.GetCustomAttributes().OfType<WolverineHttpMethodAttribute>().FirstOrDefault()))
                .Where(x => x.attr is not null)
                .Select(x => new object[]
                {
                    x.attr!.HttpMethod.ToUpperInvariant(),
                    x.attr.Template,
                    x.method.GetCustomAttribute<AllowAnonymousAttribute>() is not null || x.type.GetCustomAttribute<AllowAnonymousAttribute>() is not null,
                }));

    [Theory]
    [MemberData(nameof(RoutableEndpoints))]
    public void Every_routable_endpoint_is_described_with_matching_auth(string method, string template, bool anonymous)
    {
        var operation = OperationAt(method, template);
        operation.ShouldNotBeNull($"the OpenAPI document is missing {method} {template} — every routable endpoint must be in the spec");

        // Auth accuracy: an anonymous endpoint carries an explicit empty `security` (opts out); every other endpoint requires
        // one of the two API-key schemes. This is the same anonymous/authenticated split the endpoint-enumeration auth test enforces.
        var security = operation!["security"]!.AsArray();
        if (anonymous)
        {
            security.Count.ShouldBe(0, $"{method} {template} is anonymous but the spec requires auth");
        }
        else
        {
            security.Count.ShouldBe(2, $"{method} {template} requires auth but the spec does not (expected bearer + apiKey)");
            operation["responses"]!.AsObject().ContainsKey("401").ShouldBeTrue($"{method} {template} should document 401");
        }
    }

    [Fact]
    public void The_spec_describes_no_endpoint_that_is_not_routable()
    {
        var routable = RoutableEndpoints()
            .Select(row => ((string)row[0], (string)row[1]))
            .ToHashSet();

        var described = DocumentedOperations().Select(op => (op.Method, op.Path)).ToHashSet();

        described.ShouldBe(routable, ignoreOrder: true);
    }

    [Fact]
    public void The_payload_schema_url_matches_the_served_schema_route()
    {
        // The DSL $ref must point at the route SchemaEndpoint actually serves (issue #20), so the reference never dangles.
        var schemaRoute = RoutableEndpoints().Single(row => ((string)row[1]).StartsWith("/schema/", StringComparison.Ordinal))[1];
        OpenApiSpec.PayloadSchemaUrl.ShouldBe(schemaRoute);

        // And every payload-carrying request body references it rather than restating the DSL.
        foreach (var component in new[] { "StartRunRequest", "SavePayloadRequest", "RevisePayloadRequest" })
        {
            _document["components"]!["schemas"]![component]!["properties"]!["payload"]!["$ref"]!.GetValue<string>()
                .ShouldBe(OpenApiSpec.PayloadSchemaUrl);
        }
    }

    [Fact]
    public void Every_component_ref_resolves_to_a_declared_schema()
    {
        var declared = _document["components"]!["schemas"]!.AsObject().Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

        var dangling = ComponentRefs(_document)
            .Where(name => !declared.Contains(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        dangling.ShouldBeEmpty($"these $ref targets are not declared components: {string.Join(", ", dangling)}");
    }

    [Fact]
    public void Every_path_parameter_is_declared_on_its_operation()
    {
        foreach (var op in DocumentedOperations())
        {
            var expected = _pathParam.Matches(op.Path).Select(m => m.Groups["name"].Value).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var declared = (op.Operation["parameters"]?.AsArray() ?? [])
                .Where(p => string.Equals(p!["in"]!.GetValue<string>(), "path", StringComparison.Ordinal))
                .Select(p => p!["name"]!.GetValue<string>())
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            declared.ShouldBe(expected, $"{op.Method} {op.Path} path parameters are not fully declared");
        }
    }

    [Fact]
    public void The_run_admission_shapes_are_documented()
    {
        var startRun = OperationAt("POST", "/runs")!["responses"]!.AsObject();

        // The three run shapes + the two admission outcomes the ticket calls out.
        startRun["200"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>().ShouldEndWith("RunResponse");
        startRun["202"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>().ShouldEndWith("RunStateResponse");
        startRun["429"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>().ShouldEndWith("RunRejection");
        startRun["429"]!["description"]!.GetValue<string>().ShouldContain("queue_depth_exceeded");

        OperationAt("POST", "/runs/{id}/cancel")!["responses"]!.AsObject().ContainsKey("202").ShouldBeTrue();

        // Replay delegates to the same admission path, so it can also return 429 (and the pin-rejection 400s), not just
        // the replay-specific inline_not_replayable — the spec must reflect that.
        var replay = OperationAt("POST", "/runs/{id}/replay")!["responses"]!.AsObject();
        replay["429"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>().ShouldEndWith("RunRejection");
        replay["429"]!["description"]!.GetValue<string>().ShouldContain("queue_depth_exceeded");
        replay["400"]!["description"]!.GetValue<string>().ShouldContain("inline_not_replayable");
    }

    [Fact]
    public void Renames_400_does_not_claim_a_save_time_gate()
    {
        // Rename runs no save-time gate (LOW review nit): its PayloadValidationProblem 400 is only the archived guard.
        var rename = OperationAt("POST", "/payloads/{id}/rename")!["responses"]!["400"]!["description"]!.GetValue<string>();
        rename.ShouldContain("archived");
        rename.ShouldNotContain("save-time gate");
    }

    [Fact]
    public void Every_component_schema_is_a_valid_json_schema()
    {
        // Parse each generated/authored component with the same engine the save-time gate uses; a structurally invalid schema throws.
        foreach (var (name, schema) in _document["components"]!["schemas"]!.AsObject())
        {
            Should.NotThrow(() => JsonSchema.FromText(schema!.ToJsonString()), $"component {name} is not a valid JSON Schema");
        }
    }

    [Fact]
    public void Representative_wire_bodies_validate_against_their_component_schema()
    {
        // Guards the class of drift the reviewer found: JsonSchemaExporter marks [JsonIgnore(WhenWritingNull)] fields as
        // `required`, so a running run ({runId,status}) or an added diff entry (no `from`) would fail its own schema. Each body
        // below is a REAL DTO serialized with the live wire options (web defaults + ContractsJson), evaluated against the
        // generated component schema with the same engine the save-time gate uses.
        var wire = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        ContractsJson.Configure(wire);

        var id = Guid.NewGuid();
        var stats = new RunStats(12, 3, 4, 1, 0);
        var failure = new RunFailureDetail("terminal", "nav_failed", "boom", new RunStepRef(1, "navigate"));
        var result = JsonSerializer.SerializeToElement(new { rows = 2 });
        var scriptA = JsonSerializer.SerializeToElement(new { crawldad = 1 });
        var scriptB = JsonSerializer.SerializeToElement(new { crawldad = 1, name = "x" });

        var cases = new (string Component, object Body)[]
        {
            (nameof(RunStateResponse), new RunStateResponse(id, RunStatus.Running, null, null, null, null)),                 // 202 running: { runId, status }
            (nameof(RunStateResponse), new RunStateResponse(id, RunStatus.Queued, null, null, null, null, Position: 3)),     // 202 queued
            (nameof(RunStateResponse), new RunStateResponse(id, RunStatus.Succeeded, result, null, null, stats)),           // succeeded poll
            (nameof(RunResponse), new RunResponse(id, RunStatus.Succeeded, result, null, stats)),                           // 200 succeeded
            (nameof(RunResponse), new RunResponse(id, RunStatus.Failed, null, failure, stats)),                            // 200 failed
            (nameof(PayloadDiffResponse), new PayloadDiffResponse(id, 1, 2, scriptA, scriptB,
                [
                    new PayloadDiffEntry("/steps/0", PayloadDiffKind.Added, null, JsonSerializer.SerializeToElement("added")),
                    new PayloadDiffEntry("/name", PayloadDiffKind.Removed, JsonSerializer.SerializeToElement("old"), null),
                ])),
        };

        foreach (var (component, body) in cases)
        {
            var schema = JsonSchema.FromText(_document["components"]!["schemas"]![component]!.ToJsonString());
            var json = JsonSerializer.Serialize(body, wire);
            using var instance = JsonDocument.Parse(json);
            var evaluation = schema.Evaluate(instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            evaluation.IsValid.ShouldBeTrue($"{component} body {json} must validate against its own generated schema: {JsonSerializer.Serialize(evaluation)}");
        }
    }

    [Fact]
    public void The_document_is_openapi_3_1_with_both_api_key_schemes()
    {
        _document["openapi"]!.GetValue<string>().ShouldBe("3.1.0");
        _document["info"]!["title"]!.GetValue<string>().ShouldBe("Crawldad HTTP API");

        var schemes = _document["components"]!["securitySchemes"]!.AsObject();
        schemes["bearerAuth"]!["scheme"]!.GetValue<string>().ShouldBe("bearer");
        schemes["apiKeyAuth"]!["name"]!.GetValue<string>().ShouldBe("X-Api-Key");
    }

    private static JsonNode? OperationAt(string method, string path)
    {
        if (_document["paths"]![path] is not JsonObject item)
        {
            return null;
        }

        // Match the OpenAPI method key (lowercase) against the HTTP method case-insensitively, without normalizing case.
        return item.FirstOrDefault(kv => string.Equals(kv.Key, method, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static IEnumerable<(string Method, string Path, JsonNode Operation)> DocumentedOperations()
    {
        foreach (var path in _document["paths"]!.AsObject())
        {
            foreach (var op in path.Value!.AsObject())
            {
                yield return (op.Key.ToUpperInvariant(), path.Key, op.Value!);
            }
        }
    }

    private static IEnumerable<string> ComponentRefs(JsonNode? node)
    {
        const string prefix = "#/components/schemas/";
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj)
                {
                    if (string.Equals(kv.Key, "$ref", StringComparison.Ordinal) && kv.Value is JsonValue value && value.GetValue<string>() is { } target && target.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        yield return target[prefix.Length..];
                    }

                    foreach (var nested in ComponentRefs(kv.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var nested in ComponentRefs(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
