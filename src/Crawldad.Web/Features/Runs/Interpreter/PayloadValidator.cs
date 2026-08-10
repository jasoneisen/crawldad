using System.Text.Json;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>One validation problem found in a payload: a JSON-Pointer <see cref="Path"/> into the document, a stable
/// <see cref="Code"/>, and a human message. <see cref="StepIndex"/>/<see cref="StepKind"/> pinpoint the enclosing
/// top-level step and offending node head so the run-time pre-pass can surface <c>failure.atStep</c>.</summary>
internal sealed record PayloadIssue(string Path, string Code, string Message, int StepIndex, string StepKind);

/// <summary>The declared <c>secretRef</c> inputs of a payload: the single parse of the <c>inputs</c> block shared
/// by the interpreter (which excludes them from the eval scope and resolves them at <c>fill.secret</c>) and the semantic
/// walker (which rejects them anywhere in the expression value space), so the two never disagree on what is a secret.</summary>
internal static class SecretRefInputs
{
    /// <summary>The <c>secretRef</c>-typed input names, or an empty set when none are declared.</summary>
    public static IReadOnlySet<string> Names(JsonElement payload)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (payload.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Object)
        {
            foreach (var declared in inputs.EnumerateObject())
            {
                if (declared.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                    && string.Equals(type.GetString(), "secretRef", StringComparison.Ordinal))
                {
                    names.Add(declared.Name);
                }
            }
        }

        return names;
    }
}

/// <summary>The canonical set of recognised node head keys — the single source of truth shared by the interpreter's
/// dispatch, the structural validator, and (by construction) <c>schema/crawldad-1.schema.json</c>. A head outside
/// this set is <c>unknown_node</c>; any executable head missing from it would be rejected by the node tests.</summary>
internal static class NodeHeads
{
    /// <summary>The recognised node heads.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "comment", "goto", "waitForLoadState", "waitForRequest", "waitFor", "frame", "addStyleTag",
        "click", "fill", "clear", "screenshot", "locate", "download", "set", "push", "log", "guard", "fail",
        "if", "switch", "loop", "forEach", "break", "continue", "checkpoint",
    };
}

/// <summary>The one payload validator, shared by save-time and run-time so the two never diverge.
/// <see cref="ValidateStructure"/> rejects unknown heads and missing <c>maxIterations</c> (used standalone by the
/// run-time pre-pass); <see cref="Validate"/> adds the semantic pass, reusing the real parsers — never a second grammar.</summary>
internal static class PayloadValidator
{
    /// <summary>Structural pre-pass: unknown heads + missing <c>maxIterations</c>, in DFS order. Assumes a <c>steps</c>
    /// array of single-head node objects (the JSON Schema guarantees this at save time); collects issues instead of
    /// throwing on the first. Used standalone by the run-time pre-pass and folded into <see cref="Validate"/>.</summary>
    public static IReadOnlyList<PayloadIssue> ValidateStructure(JsonElement payload)
    {
        var issues = new List<PayloadIssue>();
        var index = 0;
        foreach (var step in payload.GetProperty("steps").EnumerateArray())
        {
            ValidateNodeStructure(step, index, $"/steps/{index}", issues);
            index++;
        }

        return issues;
    }

    /// <summary>Full save-time validation: the structural pre-pass plus the semantic pass. The payload is assumed
    /// schema-valid (all required fields present, correctly typed).</summary>
    public static IReadOnlyList<PayloadIssue> Validate(JsonElement payload)
    {
        var issues = new List<PayloadIssue>(ValidateStructure(payload));
        new SemanticWalker(issues).Walk(payload);
        return issues;
    }

    // ----- structural pass (the extracted ValidateProgram) -------------------

    private static void ValidateNodeStructure(JsonElement node, int stepIndex, string path, List<PayloadIssue> issues)
    {
        var head = node.EnumerateObject().First().Name;
        if (!NodeHeads.All.Contains(head))
        {
            issues.Add(new PayloadIssue(path, InterpreterErrorCodes.UnknownNode, $"unknown node '{head}'", stepIndex, head));
            return; // an unknown node has no known shape to descend into.
        }

        if (string.Equals(head, "comment", StringComparison.Ordinal))
        {
            return; // comment is a no-op annotation, exempt and with a bare-string body.
        }

        var body = node.GetProperty(head);
        if (RequiresMaxIterations(head) && !body.TryGetProperty("maxIterations", out _))
        {
            issues.Add(new PayloadIssue($"{path}/{head}", InterpreterErrorCodes.MissingMaxIterations, $"'{head}' requires a maxIterations cap (§6)", stepIndex, head));
        }

        ValidateChildBlocksStructure(body, $"{path}/{head}", stepIndex, issues);
    }

    private static bool RequiresMaxIterations(string head) =>
        string.Equals(head, "loop", StringComparison.Ordinal) || string.Equals(head, "forEach", StringComparison.Ordinal);

    private static void ValidateChildBlocksStructure(JsonElement body, string path, int stepIndex, List<PayloadIssue> issues)
    {
        ValidateBlockStructure(body, "then", path, stepIndex, issues);
        ValidateBlockStructure(body, "else", path, stepIndex, issues);
        ValidateBlockStructure(body, "do", path, stepIndex, issues);
        ValidateBlockStructure(body, "trigger", path, stepIndex, issues);
        ValidateBlockStructure(body, "resume", path, stepIndex, issues); // a checkpoint's resume sub-program
        ValidateBlockStructure(body, "default", path, stepIndex, issues);
        if (body.TryGetProperty("cases", out var cases))
        {
            var caseIndex = 0;
            foreach (var branch in cases.EnumerateArray())
            {
                ValidateBlockStructure(branch, "do", $"{path}/cases/{caseIndex}", stepIndex, issues);
                caseIndex++;
            }
        }
    }

    private static void ValidateBlockStructure(JsonElement owner, string name, string path, int stepIndex, List<PayloadIssue> issues)
    {
        if (owner.TryGetProperty(name, out var block))
        {
            var index = 0;
            foreach (var node in block.EnumerateArray())
            {
                ValidateNodeStructure(node, stepIndex, $"{path}/{name}/{index}", issues);
                index++;
            }
        }
    }
}
