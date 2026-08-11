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
    /// <summary>The <c>secretRef</c>-typed input names, or an empty set when none are declared. Total on an UNVALIDATED
    /// inline payload — a non-object <c>inputs</c> block or declaration is simply skipped (it is not a secretRef and
    /// TryGetProperty would throw on a non-object receiver); the structural pre-pass classifies the malformation.</summary>
    public static IReadOnlySet<string> Names(JsonElement payload)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (payload.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Object)
        {
            foreach (var declared in inputs.EnumerateObject())
            {
                if (declared.Value.ValueKind == JsonValueKind.Object
                    && declared.Value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
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
        "click", "fill", "clear", "screenshot", "locate", "download", "capture", "set", "push", "log", "guard", "fail",
        "if", "switch", "loop", "forEach", "break", "continue", "checkpoint",
    };
}

/// <summary>The one payload validator, shared by save-time and run-time so the two never diverge.
/// <see cref="ValidateStructure"/> rejects unknown heads and missing <c>maxIterations</c> (used standalone by the
/// run-time pre-pass); <see cref="Validate"/> adds the semantic pass, reusing the real parsers — never a second grammar.</summary>
internal static class PayloadValidator
{
    /// <summary>Structural pre-pass: config/steps shape + unknown heads + missing <c>maxIterations</c>, in DFS order. The
    /// JSON Schema guarantees the shape at save time, but the run-time pre-pass runs on UNVALIDATED inline JSON, so every
    /// enumeration is kind-guarded — a wrong-kinded step/block/node classifies as an issue, never a raw-accessor 500.
    /// <c>vars</c>/<c>result</c> are shape-checked lazily at their evaluation, matching the run path's fault-at-eval rule.</summary>
    public static IReadOnlyList<PayloadIssue> ValidateStructure(JsonElement payload)
    {
        // config is read (backend/retry/session/screenshot) BEFORE the backend connect, and steps is iterated right here,
        // so both are shape-checked eagerly; vars/result are evaluated later and stay lazy so a run that faults first is
        // not pre-empted by an eager check (the reference's own fault ordering).
        var issues = new List<PayloadIssue>();
        if (!payload.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object)
        {
            issues.Add(TopLevel("/config", "config must be an object", "config"));
        }

        // The inputs block declares input types; the interpreter ctor reads it for secretRef detection BEFORE the
        // classified region, so its shape is validated eagerly here — a non-object block/declaration classifies as
        // malformed_node rather than a ctor-time TryGetProperty throw (which no path can catch).
        if (payload.TryGetProperty("inputs", out var inputs))
        {
            ValidateInputsStructure(inputs, issues);
        }

        if (!payload.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            issues.Add(TopLevel("/steps", "steps must be an array", "steps"));
            return issues; // no steps array to descend into
        }

        var index = 0;
        foreach (var step in steps.EnumerateArray())
        {
            ValidateNodeStructure(step, index, $"/steps/{index}", issues);
            index++;
        }

        return issues;
    }

    private static PayloadIssue TopLevel(string path, string message, string kind) =>
        new(path, InterpreterErrorCodes.MalformedNode, message, 0, kind);

    // The inputs block must be an object of object-valued declarations (each `{ "type": ... }`). Mirrors the shape
    // SecretRefInputs.Names now skips defensively, so a malformation surfaces as a classified issue, not a silent skip.
    private static void ValidateInputsStructure(JsonElement inputs, List<PayloadIssue> issues)
    {
        if (inputs.ValueKind != JsonValueKind.Object)
        {
            issues.Add(TopLevel("/inputs", "inputs must be an object", "inputs"));
            return;
        }

        foreach (var declared in inputs.EnumerateObject())
        {
            if (declared.Value.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new PayloadIssue($"/inputs/{declared.Name}", InterpreterErrorCodes.MalformedNode, $"input declaration '{declared.Name}' must be an object", 0, "inputs"));
            }
        }
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
        if (node.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new PayloadIssue(path, InterpreterErrorCodes.MalformedNode, "a node must be an object", stepIndex, ""));
            return;
        }

        var head = "";
        var hasHead = false;
        foreach (var property in node.EnumerateObject())
        {
            head = property.Name;
            hasHead = true;
            break;
        }

        if (!hasHead)
        {
            issues.Add(new PayloadIssue(path, InterpreterErrorCodes.MalformedNode, "a node needs a single head key", stepIndex, ""));
            return;
        }

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
        if (body.ValueKind != JsonValueKind.Object)
        {
            issues.Add(new PayloadIssue($"{path}/{head}", InterpreterErrorCodes.MalformedNode, $"the '{head}' body must be an object", stepIndex, head));
            return; // a non-object body has no fields/blocks to descend into.
        }

        if (RequiresMaxIterations(head) && !body.TryGetProperty("maxIterations", out _))
        {
            issues.Add(new PayloadIssue($"{path}/{head}", InterpreterErrorCodes.MissingMaxIterations, $"'{head}' requires a maxIterations cap", stepIndex, head));
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
        if (!body.TryGetProperty("cases", out var cases))
        {
            return;
        }

        if (cases.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new PayloadIssue($"{path}/cases", InterpreterErrorCodes.MalformedNode, "'cases' must be an array", stepIndex, "switch"));
            return;
        }

        var caseIndex = 0;
        foreach (var branch in cases.EnumerateArray())
        {
            if (branch.ValueKind == JsonValueKind.Object)
            {
                ValidateBlockStructure(branch, "do", $"{path}/cases/{caseIndex}", stepIndex, issues);
            }
            else
            {
                issues.Add(new PayloadIssue($"{path}/cases/{caseIndex}", InterpreterErrorCodes.MalformedNode, "a switch case must be an object", stepIndex, "switch"));
            }

            caseIndex++;
        }
    }

    // `owner` (a node body or a switch case) is already known to be an object; a present block that is not an array is
    // an issue rather than a raw EnumerateArray throw, and an absent one is simply not descended into.
    private static void ValidateBlockStructure(JsonElement owner, string name, string path, int stepIndex, List<PayloadIssue> issues)
    {
        if (!owner.TryGetProperty(name, out var block))
        {
            return;
        }

        if (block.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new PayloadIssue($"{path}/{name}", InterpreterErrorCodes.MalformedNode, $"'{name}' must be an array", stepIndex, name));
            return;
        }

        var index = 0;
        foreach (var node in block.EnumerateArray())
        {
            ValidateNodeStructure(node, stepIndex, $"{path}/{name}/{index}", issues);
            index++;
        }
    }
}
