using System.Text.Json;
using Crawldad.Web.Features.Runs.Interpreter.Expressions;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>
/// The save-time semantic pass (§12): walks a schema-valid payload in execution order and, for every leaf expression,
/// template, and <c>set</c> path, (a) parses it through the <b>real</b> <see cref="CrawldadExpression"/>/
/// <see cref="CrawldadTemplate"/>/<see cref="SetPath"/> parser — surfacing unknown-builtin/wrong-arity/syntax errors —
/// and (b) checks that every free identifier and every structural var/frame reference (<c>in</c>/<c>from</c>/
/// <c>base</c>/<c>into</c>) is defined before use. Ordinary vars (<c>set</c>/<c>locate</c>/<c>frame</c>/<c>download</c>
/// targets and declared <c>vars</c>) accrue and persist (the flat run scope, §8.2); loop/binding variables are scoped
/// to their body. <c>input</c> is always in scope; its sub-keys are not checked (an absent input key is null at run
/// time, not a failure). Bare-string node selectors are var-first at run time, so they are treated as templates and
/// their (non-interpolated) text is not resolved as a reference — matching run-time leniency. It also (c) enforces the
/// §11 checkpoint-placement rules the resume machinery can honour — a <c>checkpoint</c> must head a single, top-level
/// <c>while</c> loop and be the only one there — rejecting anything the top-level-step cursor model cannot re-enter.
/// </summary>
internal sealed class SemanticWalker
{
    private readonly List<PayloadIssue> _issues;
    private readonly HashSet<string> _inScope = new(StringComparer.Ordinal) { "input" };
    private IReadOnlySet<string> _secretRefInputs = new HashSet<string>(StringComparer.Ordinal);
    private int _stepIndex = -1;
    private string _stepKind = "";

    // ----- checkpoint-placement context (§11) --------------------------------
    // The number of loops (loop/forEach) enclosing the current node, counted through normal control blocks only.
    private int _loopDepth;

    // Classification of the OUTERMOST enclosing loop (meaningful when _loopDepth >= 1): whether it is a `while`-form
    // `loop`, and whether it is a direct top-level step. A checkpoint is resumable only under a top-level `while` loop.
    private bool _outermostLoopIsWhile;
    private bool _outermostLoopIsTopLevel;

    // Whether the current node is inside a re-established sub-program (a checkpoint `resume` or an action `trigger`),
    // where a checkpoint may never appear — those blocks re-run on resume and persist no checkpoint of their own.
    private bool _inReestablishBlock;

    // Whether the current node is a direct top-level step (a member of `steps`), not nested inside any block.
    private bool _atTopLevel;

    // Checkpoints already seen in the current top-level step — the resume unit permits at most one (reset per step).
    private int _checkpointsInStep;

    // The mutually-exclusive structured-Sel root keys (§5.2), in the resolver's precedence order.
    private static readonly string[] _selRootKeys = ["css", "xpath", "text", "role", "title", "base"];

    public SemanticWalker(List<PayloadIssue> issues) => _issues = issues;

    /// <summary>Validates the whole payload: <c>config.backend</c>, then <c>vars</c> (in order), then <c>steps</c>, then <c>result</c>.</summary>
    /// <param name="payload">The schema-valid payload document.</param>
    public void Walk(JsonElement payload)
    {
        // CD-6: the declared secretRef inputs — the only inputs a fill.secret may reference, and the ones no expression/template
        // may name (the structural guarantee that a secret never enters the expression value space).
        _secretRefInputs = SecretRefInputs.Names(payload);

        // config.backend resolves before vars, so it may reference only input (§8.1 order).
        _stepKind = "config";
        CheckExpr(payload.GetProperty("config").GetProperty("backend").GetString()!, "/config/backend");

        _stepKind = "vars";
        if (payload.TryGetProperty("vars", out var vars))
        {
            foreach (var declared in vars.EnumerateObject())
            {
                // A string var value is an Expr evaluated against input + previously-declared vars; a non-string is a JSON literal.
                if (declared.Value.ValueKind == JsonValueKind.String)
                {
                    CheckExpr(declared.Value.GetString()!, $"/vars/{declared.Name}");
                }

                _inScope.Add(declared.Name);
            }
        }

        var index = 0;
        foreach (var step in payload.GetProperty("steps").EnumerateArray())
        {
            _stepIndex = index;
            _checkpointsInStep = 0; // §11: at most one checkpoint per top-level step (the resume unit)
            _atTopLevel = true; // a direct member of `steps`; WalkBlock clears this as it descends
            WalkNode(step, $"/steps/{index}");
            index++;
        }

        _stepIndex = -1;
        _stepKind = "result";
        CheckExpr(payload.GetProperty("result").GetString()!, "/result");
    }

    // ----- node dispatch -----------------------------------------------------

    private void WalkNode(JsonElement node, string path)
    {
        string head = "";
        foreach (var property in node.EnumerateObject())
        {
            head = property.Name;
            break;
        }

        _stepKind = head;
        if (string.Equals(head, "comment", StringComparison.Ordinal))
        {
            return; // §6: no-op annotation.
        }

        var body = node.GetProperty(head);
        var p = $"{path}/{head}";
        switch (head)
        {
            case "goto":
            case "waitForLoadState":
            case "waitForRequest":
            case "waitFor":
            case "frame":
            case "addStyleTag":
            case "click":
            case "fill":
            case "clear":
            case "screenshot":
                WalkInteractionNode(head, body, p);
                break;
            case "locate":
            case "download":
            case "set":
            case "push":
            case "log":
            case "fail":
            case "guard":
                WalkStateNode(head, body, p);
                break;
            case "checkpoint":
                WalkCheckpoint(body, p);
                break;
            default:
                WalkControlNode(head, body, p); // if/switch/loop/forEach/break/continue
                break;
        }
    }

    // Navigation + DOM interaction nodes (§5.1): selector/template/frame leaves, no scope mutation except `frame`.
    private void WalkInteractionNode(string head, JsonElement body, string p)
    {
        switch (head)
        {
            case "goto":
                CheckTmpl(body.GetProperty("url").GetString()!, $"{p}/url");
                break;
            case "waitForLoadState":
                break; // only an enum state + optional timeout.
            case "waitForRequest":
                CheckTmpl(body.GetProperty("urlPrefix").GetString()!, $"{p}/urlPrefix");
                WalkReestablishBlock(body.GetProperty("trigger"), $"{p}/trigger");
                break;
            case "waitFor":
                CheckSel(body.GetProperty("selector"), $"{p}/selector");
                CheckNodeIn(body, p);
                break;
            case "frame":
                CheckTmpl(body.GetProperty("selector").GetString()!, $"{p}/selector");
                Define(body);
                break;
            case "addStyleTag":
                CheckTmpl(body.GetProperty("content").GetString()!, $"{p}/content");
                break;
            case "click":
                CheckSel(body.GetProperty("selector"), $"{p}/selector");
                CheckNodeIn(body, p);
                break;
            case "fill":
                CheckSel(body.GetProperty("selector"), $"{p}/selector");
                CheckNodeIn(body, p);
                if (body.TryGetProperty("secret", out var fillSecret))
                {
                    CheckFillSecret(fillSecret.GetString()!, $"{p}/secret"); // CD-6: a restricted secretRef reference, never an Expr
                }
                else
                {
                    CheckExpr(body.GetProperty("value").GetString()!, $"{p}/value");
                }

                break;
            case "screenshot":
                // #8: a full-page capture with no scope effect; its only leaf is the optional author `name` label (a Tmpl,
                // interpolation-consistent with log.message/goto.url), so a bad reference in it is caught at save time.
                if (body.TryGetProperty("name", out var shotName))
                {
                    CheckTmpl(shotName.GetString()!, $"{p}/name");
                }

                break;
            default: // clear
                CheckSel(body.GetProperty("selector"), $"{p}/selector");
                CheckNodeIn(body, p);
                break;
        }
    }

    // State + logging + failure nodes (§5.1/§6): expression leaves and the var-defining nodes.
    private void WalkStateNode(string head, JsonElement body, string p)
    {
        switch (head)
        {
            case "locate":
                WalkLocate(body, p);
                break;
            case "download":
                CheckExpr(body.GetProperty("to").GetString()!, $"{p}/to");
                WalkReestablishBlock(body.GetProperty("trigger"), $"{p}/trigger");
                Define(body);
                break;
            case "set":
                CheckExpr(body.GetProperty("value").GetString()!, $"{p}/value");
                if (body.TryGetProperty("path", out var setPath))
                {
                    CheckPath(setPath.GetString()!, $"{p}/path");
                }

                Define(body);
                break;
            case "push":
                CheckRef(body.GetProperty("into").GetString()!, $"{p}/into");
                CheckExpr(body.GetProperty("value").GetString()!, $"{p}/value");
                break;
            case "log":
                CheckTmpl(body.GetProperty("message").GetString()!, $"{p}/message");
                break;
            case "fail":
                CheckTmpl(body.GetProperty("message").GetString()!, $"{p}/message");
                break;
            default: // guard
                CheckExpr(body.GetProperty("cond").GetString()!, $"{p}/cond");
                CheckTmpl(body.GetProperty("elseFail").GetProperty("message").GetString()!, $"{p}/elseFail/message");
                break;
        }
    }

    // Control-flow nodes (§6): the expression predicates and the recursed child blocks.
    private void WalkControlNode(string head, JsonElement body, string p)
    {
        switch (head)
        {
            case "if":
                CheckExpr(body.GetProperty("cond").GetString()!, $"{p}/cond");
                WalkBlock(body.GetProperty("then"), $"{p}/then");
                if (body.TryGetProperty("else", out var elseBlock))
                {
                    WalkBlock(elseBlock, $"{p}/else");
                }

                break;
            case "switch":
                WalkSwitch(body, p);
                break;
            case "loop":
                WalkLoop(body, p);
                break;
            case "forEach":
                WalkForEach(body, p);
                break;
            default: // break / continue
                if (body.TryGetProperty("when", out var when))
                {
                    CheckExpr(when.GetString()!, $"{p}/when");
                }

                break;
        }
    }

    private void WalkLocate(JsonElement body, string p)
    {
        if (body.TryGetProperty("from", out var from))
        {
            CheckRef(from.GetString()!, $"{p}/from");
            if (body.TryGetProperty("filter", out var filter))
            {
                CheckTmpl(filter.GetProperty("hasTextRegex").GetString()!, $"{p}/filter/hasTextRegex");
            }

            if (body.TryGetProperty("nth", out var nth))
            {
                CheckExpr(nth.GetString()!, $"{p}/nth");
            }
        }
        else
        {
            CheckSel(body.GetProperty("selector"), $"{p}/selector");
            if (body.TryGetProperty("base", out var baseVar))
            {
                CheckRef(baseVar.GetString()!, $"{p}/base");
            }

            CheckNodeIn(body, p);
        }

        Define(body);
    }

    private void WalkSwitch(JsonElement body, string p)
    {
        var caseIndex = 0;
        foreach (var branch in body.GetProperty("cases").EnumerateArray())
        {
            CheckExpr(branch.GetProperty("when").GetString()!, $"{p}/cases/{caseIndex}/when");
            WalkBlock(branch.GetProperty("do"), $"{p}/cases/{caseIndex}/do");
            caseIndex++;
        }

        if (body.TryGetProperty("default", out var def))
        {
            WalkBlock(def, $"{p}/default");
        }
    }

    private void WalkLoop(JsonElement body, string p)
    {
        var isWhileForm = !body.TryGetProperty("for", out var forSpec);
        var loop = EnterLoop(isWhileForm); // §11: classify this loop as a checkpoint host (top-level while) or not
        if (!isWhileForm)
        {
            CheckBound(forSpec.GetProperty("from"), $"{p}/for/from");
            if (forSpec.TryGetProperty("step", out var step))
            {
                CheckBound(step, $"{p}/for/step");
            }

            var added = EnterScope(forSpec.GetProperty("var").GetString()!);
            CheckBound(forSpec.GetProperty("to"), $"{p}/for/to"); // `to` re-evaluates with the loop var in scope.
            WalkBlock(body.GetProperty("do"), $"{p}/do");
            ExitScope(added);
        }
        else
        {
            // while-form (do-while): the body runs before the test, so the test may read what the body set (§6).
            WalkBlock(body.GetProperty("do"), $"{p}/do");
            CheckExpr(body.GetProperty("while").GetString()!, $"{p}/while");
        }

        ExitLoop(loop);
    }

    // checkpoint (§11): the cursor Expr resolves against the current scope; the resume sub-program runs only on resume
    // with the restored cursor bound to the `checkpoint` var, so that name is in scope for the resume block alone.
    private void WalkCheckpoint(JsonElement body, string p)
    {
        ValidateCheckpointPlacement(p);
        CheckExpr(body.GetProperty("cursor").GetString()!, $"{p}/cursor");
        if (body.TryGetProperty("resume", out var resume))
        {
            var added = EnterScope(RunInterpreter.CheckpointCursorVar);
            WalkReestablishBlock(resume, $"{p}/resume"); // a nested checkpoint here would be re-run on resume — reject it
            ExitScope(added);
        }
    }

    // The §11 placement rules the resume machinery can actually honour, derived from the interpreter: resume re-enters at
    // the checkpoint's enclosing TOP-LEVEL step (RunInterpreter._currentStepIndex only tracks the top level), re-runs it
    // against a fresh session, restores ONE stored cursor + var snapshot, and the FIRST checkpoint reached consumes the
    // resume. So a checkpoint is resumable only when it heads a single, top-level `while` loop and is the only checkpoint
    // in that step. A `for`/`forEach` re-initialises its counter from `from`/`in` at entry (RunScope.Shadow overwrites any
    // restored value), so its iteration cannot be resumed; a nested/second loop or a loop below a top-level step cannot be
    // re-entered directly; and a `resume`/`trigger` sub-program persists no checkpoint of its own.
    private void ValidateCheckpointPlacement(string p)
    {
        _checkpointsInStep++;
        if (_inReestablishBlock)
        {
            Misplaced(p, "a checkpoint may not appear inside a resume or trigger sub-program — those re-run on resume and record no checkpoint of their own");
        }
        else if (_loopDepth == 0)
        {
            Misplaced(p, "a checkpoint must sit inside a top-level while loop — it heads no loop here, so there is no iteration to resume");
        }
        else if (_loopDepth > 1)
        {
            Misplaced(p, "a checkpoint may not sit inside a nested loop — resume re-enters only at the top-level step, so an inner loop's position cannot be restored");
        }
        else if (!_outermostLoopIsWhile)
        {
            Misplaced(p, "a checkpoint's loop must be a while loop — a for/forEach re-initialises its counter on resume, so its iteration cannot be resumed");
        }
        else if (!_outermostLoopIsTopLevel)
        {
            Misplaced(p, "a checkpoint's while loop must be a top-level step — resume re-enters at the top level, so a loop nested below it cannot be re-entered directly");
        }
        else if (_checkpointsInStep > 1)
        {
            _issues.Add(new PayloadIssue(
                p, InterpreterErrorCodes.CheckpointNotUnique,
                "at most one checkpoint may appear per top-level loop — resume restores a single cursor and re-enters at the first checkpoint reached", _stepIndex, _stepKind));
        }
    }

    private void Misplaced(string path, string message) =>
        _issues.Add(new PayloadIssue(path, InterpreterErrorCodes.CheckpointMisplaced, message, _stepIndex, _stepKind));

    private void WalkForEach(JsonElement body, string p)
    {
        CheckExpr(body.GetProperty("in").GetString()!, $"{p}/in");

        var loop = EnterLoop(isWhileForm: false); // §11: forEach re-iterates from index 0 on resume — never a checkpoint host
        var added = body.TryGetProperty("index", out var index)
            ? EnterScope(body.GetProperty("as").GetString()!, index.GetString()!)
            : EnterScope(body.GetProperty("as").GetString()!);
        WalkBlock(body.GetProperty("do"), $"{p}/do");
        ExitScope(added);
        ExitLoop(loop);
    }

    private void WalkBlock(JsonElement block, string path)
    {
        var savedTopLevel = _atTopLevel;
        _atTopLevel = false; // any node inside a block is nested, not a direct top-level step
        var index = 0;
        foreach (var node in block.EnumerateArray())
        {
            WalkNode(node, $"{path}/{index}");
            index++;
        }

        _atTopLevel = savedTopLevel;
    }

    // A re-established sub-program (a checkpoint `resume` or an action `trigger`): walked like any block, but a checkpoint
    // inside it is rejected (§11) — the block re-runs on resume and records no checkpoint of its own.
    private void WalkReestablishBlock(JsonElement block, string path)
    {
        var saved = _inReestablishBlock;
        _inReestablishBlock = true;
        WalkBlock(block, path);
        _inReestablishBlock = saved;
    }

    // ----- checkpoint-placement tracking (§11) -------------------------------

    // Records entry into a loop for checkpoint-placement tracking: classifies the OUTERMOST enclosing loop as a checkpoint
    // host (a top-level-step `while` loop) and deepens the nesting. Returns the prior context to restore on exit.
    private (int Depth, bool While, bool TopLevel) EnterLoop(bool isWhileForm)
    {
        var saved = (_loopDepth, _outermostLoopIsWhile, _outermostLoopIsTopLevel);
        if (_loopDepth == 0)
        {
            _outermostLoopIsWhile = isWhileForm;
            _outermostLoopIsTopLevel = _atTopLevel;
        }

        _loopDepth++;
        return saved;
    }

    private void ExitLoop((int Depth, bool While, bool TopLevel) saved) =>
        (_loopDepth, _outermostLoopIsWhile, _outermostLoopIsTopLevel) = saved;

    // ----- leaf checks -------------------------------------------------------

    private void CheckExpr(string source, string path)
    {
        CrawldadExpression expr;
        try
        {
            expr = CrawldadExpression.Parse(source);
        }
        catch (ExpressionParseException ex)
        {
            _issues.Add(new PayloadIssue(path, ex.Code, ex.Message, _stepIndex, _stepKind));
            return;
        }

        CheckDefined(expr.FreeIdentifiers(), path);
        CheckNoSecretRefs(expr.InputMemberReferences(), path);
    }

    // A loop-for bound (from/to/step, CD-10) is an Expr string or a typed JSON number literal. Both are checked through
    // the same expression parser — the number via its raw text — so a typed number N is validated exactly as the Expr
    // "N" of the same digits: a plain literal has no free identifiers and validates clean, while an unparseable one is
    // rejected here at save time, just like its Expr-string spelling.
    private void CheckBound(JsonElement bound, string path) =>
        CheckExpr(bound.ValueKind == JsonValueKind.String ? bound.GetString()! : bound.GetRawText(), path);

    private void CheckTmpl(string source, string path)
    {
        CrawldadTemplate template;
        try
        {
            template = CrawldadTemplate.Parse(source);
        }
        catch (ExpressionParseException ex)
        {
            _issues.Add(new PayloadIssue(path, ex.Code, ex.Message, _stepIndex, _stepKind));
            return;
        }

        CheckDefined(template.FreeIdentifiers(), path);
        CheckNoSecretRefs(template.InputMemberReferences(), path);
    }

    // CD-6: a fill.secret is a restricted reference, not an Expr — it must be exactly `input.<name>` naming a declared
    // secretRef input. Anything else (a general expression, or a reference to a non-secretRef input) is rejected, so the
    // secret channel and the expression value space stay disjoint.
    private void CheckFillSecret(string source, string path)
    {
        CrawldadExpression reference;
        try
        {
            reference = CrawldadExpression.Parse(source);
        }
        catch (ExpressionParseException ex)
        {
            _issues.Add(new PayloadIssue(path, ex.Code, ex.Message, _stepIndex, _stepKind));
            return;
        }

        if (!reference.TryGetInputMemberReference(out var name) || !_secretRefInputs.Contains(name))
        {
            _issues.Add(new PayloadIssue(
                path, InterpreterErrorCodes.FillSecretNotSecretRef,
                "fill.secret must reference a declared secretRef input via 'input.<name>'", _stepIndex, _stepKind));
        }
    }

    // CD-6: reject a secretRef input named anywhere in the expression value space — a secretRef may be consumed ONLY by
    // fill.secret, so a secret can never be interpolated into a log/result/selector or otherwise routed through an Expr.
    private void CheckNoSecretRefs(IReadOnlySet<string> inputMembers, string path)
    {
        if (_secretRefInputs.Count == 0)
        {
            return;
        }

        foreach (var name in inputMembers)
        {
            if (_secretRefInputs.Contains(name))
            {
                _issues.Add(new PayloadIssue(
                    path, InterpreterErrorCodes.SecretRefInExpression,
                    $"secretRef input '{name}' can only be used in fill.secret, not in an expression", _stepIndex, _stepKind));
            }
        }
    }

    private void CheckPath(string source, string path)
    {
        IReadOnlyList<PathSegment> segments;
        try
        {
            segments = SetPath.Parse(source);
        }
        catch (ExpressionParseException ex)
        {
            _issues.Add(new PayloadIssue(path, ex.Code, ex.Message, _stepIndex, _stepKind));
            return;
        }
        catch (InterpreterException ex)
        {
            _issues.Add(new PayloadIssue(path, ex.Code, ex.Message, _stepIndex, _stepKind));
            return;
        }

        foreach (var segment in segments)
        {
            if (segment is ComputedSegment computed)
            {
                CheckDefined(computed.Template.FreeIdentifiers(), path);
            }
        }
    }

    private void CheckSel(JsonElement selector, string path)
    {
        if (selector.ValueKind == JsonValueKind.String)
        {
            CheckTmpl(selector.GetString()!, path);
            return;
        }

        foreach (var field in selector.EnumerateObject())
        {
            switch (field.Name)
            {
                case "css":
                case "title":
                case "xpath":
                case "text":
                case "role":
                case "name":
                case "in":
                    CheckTmpl(field.Value.GetString()!, $"{path}/{field.Name}");
                    break;
                case "base":
                    CheckRef(field.Value.GetString()!, $"{path}/base");
                    break;
                case "nth":
                    CheckExpr(field.Value.GetString()!, $"{path}/nth");
                    break;
                case "filter":
                    CheckTmpl(field.Value.GetProperty("hasTextRegex").GetString()!, $"{path}/filter/hasTextRegex");
                    break;
                default:
                    break; // `first` is a bool; no other keys pass the schema.
            }
        }

        CheckSelCombination(selector, path);
    }

    // §5.2 union coherence: a structured Sel roots at EXACTLY ONE of css/xpath/text/role/title/base — only base+css may
    // pair (css as the base handle's relative child) — and `name` is the accessible name of a `role`. Enforced here with
    // a clear message as well as in the JSON Schema, since an ambiguous selector would otherwise be resolved silently by
    // the resolver's root precedence rather than rejected.
    private void CheckSelCombination(JsonElement selector, string path)
    {
        var roots = _selRootKeys.Where(k => selector.TryGetProperty(k, out _)).ToList();
        var isBaseCss = roots.Count == 2 && roots.Contains("base", StringComparer.Ordinal) && roots.Contains("css", StringComparer.Ordinal);
        if (roots.Count > 1 && !isBaseCss)
        {
            _issues.Add(new PayloadIssue(
                path, InterpreterErrorCodes.AmbiguousSelector,
                $"a selector roots at exactly one of css/xpath/text/role/title/base (only base+css may combine); found {string.Join('+', roots)}",
                _stepIndex, _stepKind));
        }

        if (selector.TryGetProperty("name", out _) && !selector.TryGetProperty("role", out _))
        {
            _issues.Add(new PayloadIssue(
                path, InterpreterErrorCodes.AmbiguousSelector,
                "a selector 'name' is the accessible name of a 'role' — it must accompany a 'role'",
                _stepIndex, _stepKind));
        }
    }

    private void CheckNodeIn(JsonElement body, string path)
    {
        if (body.TryGetProperty("in", out var inVar))
        {
            CheckRef(inVar.GetString()!, $"{path}/in");
        }
    }

    private void CheckDefined(IReadOnlySet<string> freeIds, string path)
    {
        foreach (var id in freeIds)
        {
            CheckRef(id, path);
        }
    }

    private void CheckRef(string name, string path)
    {
        if (!_inScope.Contains(name))
        {
            _issues.Add(new PayloadIssue(path, InterpreterErrorCodes.UndefinedReference, $"'{name}' is not defined before use", _stepIndex, _stepKind));
        }
    }

    // ----- scope -------------------------------------------------------------

    private void Define(JsonElement body) => _inScope.Add(body.GetProperty("var").GetString()!);

    // Adds loop/binding names for a body, returning those actually introduced (a name already in scope is a shadow we
    // must not remove on exit).
    private List<string> EnterScope(params string[] names)
    {
        var added = new List<string>(names.Length);
        foreach (var name in names)
        {
            if (_inScope.Add(name))
            {
                added.Add(name);
            }
        }

        return added;
    }

    private void ExitScope(List<string> added)
    {
        foreach (var name in added)
        {
            _inScope.Remove(name);
        }
    }
}
