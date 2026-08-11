using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>The stable <c>code</c> slugs for terminal engine failures the interpreter raises (distinct from the
/// expression-evaluator codes, which carry their own slugs). All of these currently surface at execution time, not
/// save-time validation.</summary>
internal static class InterpreterErrorCodes
{
    /// <summary>A node's single head key is not a recognised node type.</summary>
    public const string UnknownNode = "unknown_node";

    /// <summary>A <c>loop</c>/<c>forEach</c> omitted the mandatory <c>maxIterations</c> cap.</summary>
    public const string MissingMaxIterations = "missing_max_iterations";

    /// <summary>Save-time only: an expression/selector references a var/frame/input not defined before use.</summary>
    public const string UndefinedReference = "undefined_reference";

    /// <summary>A loop exceeded its <c>maxIterations</c> cap.</summary>
    public const string MaxIterationsExceeded = "max_iterations_exceeded";

    /// <summary>The run ran more semantic steps than the server's max-steps cap — the global runaway guard the
    /// per-loop <c>maxIterations</c> cap cannot cover (many loops each under their own cap still multiply into a runaway).</summary>
    public const string MaxStepsExceeded = "max_steps_exceeded";

    /// <summary>The run's total downloaded bytes crossed the server's max-download-bytes cap — enforced as the bytes
    /// flow, so an over-cap download aborts mid-stream, never after buffering the whole body.</summary>
    public const string MaxDownloadBytesExceeded = "max_download_bytes_exceeded";

    /// <summary>The run's total captured bytes crossed the server's max-capture-bytes cap — a sibling of the download
    /// cap, bounding the serialised-document volume a <c>capture</c> channel may stream to tenant storage across a run.</summary>
    public const string MaxCaptureBytesExceeded = "max_capture_bytes_exceeded";

    /// <summary>The run appended more trace events than the server's max-events cap — a fair-use guardrail on run
    /// stream volume no legitimate run reaches.</summary>
    public const string MaxEventsExceeded = "max_events_exceeded";

    /// <summary>A <c>push</c> target is undefined or is not an array.</summary>
    public const string UndefinedPushTarget = "undefined_push_target";

    /// <summary>The result tree contained an opaque handle (handles never serialise).</summary>
    public const string HandleInResult = "handle_in_result";

    /// <summary>The run's <c>config.backend.adapter</c> has no registered backend.</summary>
    public const string UnknownBackendAdapter = "unknown_backend_adapter";

    /// <summary>The run's <c>config.backend</c> did not resolve to a <c>{ adapter, options }</c> shape.</summary>
    public const string InvalidBackendBinding = "invalid_backend_binding";

    /// <summary>A node was structurally malformed (missing/mistyped required field) — currently caught at execution time.</summary>
    public const string MalformedNode = "malformed_node";

    /// <summary>Save-time: a structured <c>Sel</c> combined more than one root selector (<c>css</c>/<c>xpath</c>/
    /// <c>text</c>/<c>role</c>/<c>title</c>/<c>base</c> — only <c>base</c>+<c>css</c> may pair), or carried a <c>name</c>
    /// without a <c>role</c>. Ambiguous selectors are rejected rather than silently resolved by precedence.</summary>
    public const string AmbiguousSelector = "ambiguous_selector";

    /// <summary>A <c>download.to</c> did not resolve to a <c>storageTarget</c> object with a string <c>kind</c>.</summary>
    public const string InvalidDownloadTarget = "invalid_download_target";

    /// <summary>A <c>download.to</c>'s <c>kind</c> has no registered <see cref="Crawldad.Web.Infrastructure.Storage.IDownloadSink"/>.</summary>
    public const string UnknownDownloadSink = "unknown_download_sink";

    /// <summary>A <c>capture.to</c> (or <c>config.captureOnFailure.to</c>) did not resolve to a <c>storageTarget</c>
    /// object with a string <c>kind</c> — the capture analogue of <see cref="InvalidDownloadTarget"/>.</summary>
    public const string InvalidCaptureTarget = "invalid_capture_target";

    /// <summary>A <c>capture.to</c> (or <c>config.captureOnFailure.to</c>)'s <c>kind</c> has no registered
    /// <see cref="Crawldad.Web.Infrastructure.Storage.IDownloadSink"/> — the capture analogue of <see cref="UnknownDownloadSink"/>.</summary>
    public const string UnknownCaptureSink = "unknown_capture_sink";

    /// <summary>Save-time: a <c>secretRef</c> input was referenced in an expression/template — a secretRef may be
    /// consumed only by <c>fill.secret</c>, keeping the secret structurally out of the expression value space.</summary>
    public const string SecretRefInExpression = "secret_ref_in_expression";

    /// <summary>Save-time + run-time: a <c>fill.secret</c> did not name a declared <c>secretRef</c> input (a malformed
    /// reference, or one pointing at an input of another type).</summary>
    public const string FillSecretNotSecretRef = "fill_secret_not_secret_ref";

    /// <summary>Run-time: a <c>fill.secret</c>'s <c>secretRef</c> input was not supplied at run start (no reference to resolve).</summary>
    public const string SecretRefMissing = "secret_ref_missing";

    /// <summary>Run-time: the <c>secretRef</c>'s vault kind has no registered <see cref="Crawldad.Web.Infrastructure.Security.ISecretStore"/> adapter.</summary>
    public const string UnknownSecretVault = "unknown_secret_vault";

    /// <summary>Run-time: the vault held no secret for the run's (tenant-scoped) reference — a fail-fast at the <c>fill</c>,
    /// naming only the (safe) reference, never the secret.</summary>
    public const string SecretUnresolved = "secret_unresolved";

    /// <summary>Save-time: a <c>checkpoint</c> node is placed where resume cannot re-enter it — outside any loop, inside
    /// a nested loop, inside a <c>for</c>/<c>forEach</c> (counter re-initialises on resume), below a top-level step, or
    /// inside a <c>resume</c>/<c>trigger</c> sub-program. Only a checkpoint heading a top-level <c>while</c> loop qualifies.</summary>
    public const string CheckpointMisplaced = "checkpoint_misplaced";

    /// <summary>Save-time: more than one <c>checkpoint</c> appears under a single top-level loop. Resume restores one
    /// stored checkpoint (cursor + var snapshot) and re-enters at the first checkpoint reached, so a second is unrepresentable.</summary>
    public const string CheckpointNotUnique = "checkpoint_not_unique";
}

/// <summary>A terminal engine failure, carrying a stable <see cref="Code"/> from <see cref="InterpreterErrorCodes"/>.
/// Never retried; surfaced in the response as <c>failure.class = "terminal"</c>.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A code is mandatory so run-failure surfacing always has one; the codeless constructors would break that.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
internal sealed class InterpreterException : Exception
{
    /// <summary>Creates a terminal engine failure.</summary>
    public InterpreterException(string code, string message)
        : base(message) => Code = code;

    /// <summary>The stable failure slug.</summary>
    public string Code { get; }
}
