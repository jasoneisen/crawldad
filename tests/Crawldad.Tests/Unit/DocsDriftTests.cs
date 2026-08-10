using System.Reflection;
using System.Text;
using System.Text.Json;
using Crawldad.Contracts.Payloads;
using Crawldad.Web.Features.Payloads;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Tests.Unit;

/// <summary>The docs-drift gate: keeps the LLM-facing docs truthful the way the coverage gate keeps the code honest.
/// Curated examples and complete <c>docs/API.md</c> payload blocks run through the real save-time gate
/// (<see cref="PayloadValidation"/>); the wire-code table is cross-checked and every schema field must carry a <c>description</c>.</summary>
public class DocsDriftTests
{
    private static readonly string _repoRoot = FindRepoRoot();

    private static string ApiReference => Path.Combine(_repoRoot, "docs", "API.md");

    private static string TunnelGuide => Path.Combine(_repoRoot, "docs", "TUNNEL_BACKEND.md");

    private static string ExamplesDir => Path.Combine(_repoRoot, "docs", "examples");

    private static string LlmsTxt => Path.Combine(_repoRoot, "llms.txt");

    // Every curated example, as theory data (so a failing one names itself).
    public static IEnumerable<object[]> ExampleFiles() =>
        Directory.EnumerateFiles(ExamplesDir, "*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new object[] { Path.GetFileName(path) });

    [Theory]
    [MemberData(nameof(ExampleFiles))]
    public void Every_curated_example_passes_the_save_time_gate(string fileName)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(ExamplesDir, fileName)));
        var problem = PayloadValidation.Validate(document.RootElement);
        problem.ShouldBeNull(Describe(fileName, problem));
    }

    [Fact]
    public void The_examples_directory_has_at_least_four_curated_payloads() =>
        Directory.EnumerateFiles(ExamplesDir, "*.json").Count().ShouldBeGreaterThanOrEqualTo(4);

    [Fact]
    public void Every_payload_block_in_the_api_reference_passes_the_save_time_gate()
    {
        var payloadBlocks = 0;
        foreach (var block in JsonBlocks(File.ReadAllText(ApiReference)))
        {
            // Every strict ```json block must at least be well-formed JSON (catches a typo'd doc example).
            using var document = JsonDocument.Parse(block);

            // A block that is a complete payload (a top-level `crawldad` key) is held to the full save-time gate; other
            // ```json blocks (request/response wire shapes) are well-formed JSON but not payloads. Illustrative snippets
            // with comments/ellipses are authored as ```jsonc and are ignored here by construction.
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("crawldad", out _))
            {
                payloadBlocks++;
                var problem = PayloadValidation.Validate(document.RootElement);
                problem.ShouldBeNull(Describe("an API.md payload block", problem));
            }
        }

        payloadBlocks.ShouldBeGreaterThan(0); // the reference must embed at least one complete, authorable payload
    }

    [Fact]
    public void Every_payload_block_in_the_tunnel_guide_passes_the_save_time_gate()
    {
        // The tunnel on-ramp guide lives outside API.md, so scan it the same way: every strict ```json block must be
        // well-formed JSON, and any complete payload (a top-level `crawldad` key) is held to the full save-time gate —
        // keeping the guide's authorable example honest under the claims-must-match-code mandate.
        var payloadBlocks = 0;
        foreach (var block in JsonBlocks(File.ReadAllText(TunnelGuide)))
        {
            using var document = JsonDocument.Parse(block);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("crawldad", out _))
            {
                payloadBlocks++;
                var problem = PayloadValidation.Validate(document.RootElement);
                problem.ShouldBeNull(Describe("a TUNNEL_BACKEND.md payload block", problem));
            }
        }

        payloadBlocks.ShouldBeGreaterThan(0); // the guide must embed at least one complete, authorable payload
    }

    [Fact]
    public void The_tunnel_guide_is_committed_and_linked_from_the_llms_index()
    {
        File.Exists(TunnelGuide).ShouldBeTrue();
        File.ReadAllText(LlmsTxt).ShouldContain("docs/TUNNEL_BACKEND.md");
    }

    [Fact]
    public void The_wire_code_table_documents_every_enumerated_failure_code()
    {
        var apiReference = File.ReadAllText(ApiReference);

        // The two central, growth-prone slug registries plus the two queue-admission codes: if a new interpreter/expression
        // code ships without a row in the wire-code table, this fails — cheap insurance against the table rotting away
        // from the contracts. (Endpoint string-literal codes and open-ended `fail`/`guard` codes have no central registry.)
        var enumerated = SlugConstantsOf(typeof(InterpreterErrorCodes))
            .Concat(SlugConstantsOf(typeof(ExpressionErrorCodes)))
            .Append(RunQueue.QueueDepthExceededCode)
            .Append(RunQueue.QueueWaitExceededCode)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var undocumented = enumerated
            .Where(code => !apiReference.Contains(code, StringComparison.Ordinal))
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        undocumented.ShouldBeEmpty($"docs/API.md §12 is missing wire codes: {string.Join(", ", undocumented)}");
    }

    [Fact]
    public void The_served_schema_describes_every_node_and_field()
    {
        using var document = JsonDocument.Parse(PayloadSchema.Json);
        var undescribed = new List<string>();
        CollectUndescribed(document.RootElement, string.Empty, undescribed);
        undescribed.ShouldBeEmpty($"schema fields without a description: {string.Join(", ", undescribed)}");
    }

    [Fact]
    public void Llms_txt_is_committed_and_points_at_the_reference_schema_and_examples()
    {
        File.Exists(LlmsTxt).ShouldBeTrue();
        var text = File.ReadAllText(LlmsTxt);
        text.ShouldContain("docs/API.md");
        text.ShouldContain("schema/crawldad-1.schema.json");
        text.ShouldContain("docs/examples/");
    }

    // Every value under any `properties` map must carry a non-empty description — the schema is the DSL reference, so
    // a new node/field without one fails the build.
    private static void CollectUndescribed(JsonElement node, string path, List<string> undescribed)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in properties.EnumerateObject())
                {
                    if (!HasDescription(field.Value))
                    {
                        undescribed.Add($"{path}/properties/{field.Name}");
                    }
                }
            }

            foreach (var member in node.EnumerateObject())
            {
                CollectUndescribed(member.Value, $"{path}/{member.Name}", undescribed);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in node.EnumerateArray())
            {
                CollectUndescribed(item, $"{path}/{index}", undescribed);
                index++;
            }
        }
    }

    private static bool HasDescription(JsonElement field) =>
        field.ValueKind == JsonValueKind.Object
        && field.TryGetProperty("description", out var description)
        && description.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(description.GetString());

    // The `public const string` slugs of a code registry (every literal is a wire code).
    private static IEnumerable<string> SlugConstantsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    // Fenced blocks whose info string is EXACTLY "json" (strict). ```jsonc / ```http / ```text are ignored. Scanned by line
    // (no Regex) so the fence match is exact and the analyzer's regex-timeout rule is moot.
    private static List<string> JsonBlocks(string markdown)
    {
        var blocks = new List<string>();
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!string.Equals(lines[i], "```json", StringComparison.Ordinal))
            {
                continue;
            }

            var body = new StringBuilder();
            for (i++; i < lines.Length && !string.Equals(lines[i], "```", StringComparison.Ordinal); i++)
            {
                body.Append(lines[i]).Append('\n');
            }

            blocks.Add(body.ToString());
        }

        return blocks;
    }

    private static string Describe(string what, PayloadValidationProblem? problem) =>
        problem is null ? string.Empty : $"{what} is not a valid payload: {string.Join("; ", problem.Errors.Select(e => $"{e.Path} [{e.Code}] {e.Message}"))}";

    // Walk up from the test output to the repo root (the directory holding Crawldad.slnx), so the docs — which live in the
    // source tree, not the test output — are found the same way in CI and locally.
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Crawldad.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"could not locate the repo root (Crawldad.slnx) above {AppContext.BaseDirectory}");
    }
}
