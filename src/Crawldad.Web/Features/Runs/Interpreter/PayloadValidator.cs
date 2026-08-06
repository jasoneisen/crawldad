using System.Text.Json;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>One validation problem found in a payload (§12): a JSON-Pointer <see cref="Path"/> into the document, a
/// stable <see cref="Code"/>, and a human message. <see cref="StepIndex"/>/<see cref="StepKind"/> pinpoint the
/// enclosing top-level step and offending node head so the run-time pre-pass can surface a §10 <c>failure.atStep</c>.</summary>
/// <param name="Path">JSON Pointer to the offending location (e.g. <c>/steps/6/loop/do/3</c>).</param>
/// <param name="Code">Stable slug (structural: <c>unknown_node</c>/<c>missing_max_iterations</c>; semantic:
/// <c>undefined_reference</c> or an expression parse code).</param>
/// <param name="Message">Human-readable description.</param>
/// <param name="StepIndex">The enclosing top-level step index.</param>
/// <param name="StepKind">The offending node's head key.</param>
internal sealed record PayloadIssue(string Path, string Code, string Message, int StepIndex, string StepKind);

/// <summary>The canonical set of recognised node head keys (§5/§6) — the single source of truth shared by the
/// interpreter's dispatch, the structural validator, and (by construction) <c>schema/crawldad-1.schema.json</c>. A head
/// outside this set is <c>unknown_node</c>. Run-time validation uses this set, so any executable head missing from it
/// would be rejected by the existing node tests.</summary>
internal static class NodeHeads
{
    /// <summary>The 23 recognised heads (P1+P2+WP1 dispatch table).</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "comment", "goto", "waitForLoadState", "waitForRequest", "waitFor", "frame", "addStyleTag",
        "click", "fill", "clear", "locate", "download", "set", "push", "log", "guard", "fail",
        "if", "switch", "loop", "forEach", "break", "continue",
    };
}

/// <summary>
/// The one payload validator (§12, Deliverable 3), shared by save-time and run-time so the two never diverge.
/// <see cref="ValidateStructure"/> is the extracted execution-time <c>ValidateProgram</c>: it rejects unknown head keys
/// and loops/forEaches missing the mandatory <c>maxIterations</c> cap — robust to any input (the run-time pre-pass
/// calls it before connect and throws on the first issue). <see cref="Validate"/> adds the save-time semantic pass —
/// defined-before-use of every var/frame/input and a parse+arity check of every expression/template/path, reusing the
/// real <see cref="CrawldadExpression"/>/<see cref="CrawldadTemplate"/>/<see cref="SetPath"/> parsers (never a second
/// grammar). <see cref="Validate"/> assumes the payload already passed the JSON Schema, so required fields are present.
/// </summary>
internal static class PayloadValidator
{
    /// <summary>Structural pre-pass: unknown heads + missing <c>maxIterations</c>, in DFS order. Mirrors the original
    /// execution-time <c>ValidateProgram</c> exactly (assumes a <c>steps</c> array of single-head node objects, as the
    /// run path always had and the JSON Schema guarantees at save time), only collecting issues instead of throwing on
    /// the first. Used by the run-time pre-pass (which throws on <c>issues[0]</c>) and folded into <see cref="Validate"/>.</summary>
    /// <param name="payload">The payload document.</param>
    /// <returns>The structural issues in document order (empty when the structure is sound).</returns>
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
    /// <param name="payload">The schema-valid payload document.</param>
    /// <returns>All issues (structural first, then semantic), empty when the payload is valid.</returns>
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
            return; // §6: comment is a no-op annotation, exempt and with a bare-string body.
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
