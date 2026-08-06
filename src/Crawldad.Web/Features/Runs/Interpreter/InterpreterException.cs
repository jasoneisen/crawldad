using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>The stable <c>code</c> slugs for terminal engine failures the interpreter raises (distinct from the
/// expression-evaluator codes, which carry their own slugs). Validation-at-save is Phase 3; in Phase 1 these all
/// surface at execution time.</summary>
internal static class InterpreterErrorCodes
{
    /// <summary>A node's single head key is not a recognised node type.</summary>
    public const string UnknownNode = "unknown_node";

    /// <summary>A <c>loop</c>/<c>forEach</c> omitted the mandatory <c>maxIterations</c> cap (§6).</summary>
    public const string MissingMaxIterations = "missing_max_iterations";

    /// <summary>A loop exceeded its <c>maxIterations</c> cap (§6 safety).</summary>
    public const string MaxIterationsExceeded = "max_iterations_exceeded";

    /// <summary>A <c>push</c> target is undefined or is not an array.</summary>
    public const string UndefinedPushTarget = "undefined_push_target";

    /// <summary>A feature parsed but is not implemented in v0 (frames/<c>in</c>, <c>set.path</c>).</summary>
    public const string NotSupportedInV0 = "not_supported_in_v0";

    /// <summary>The result tree contained an opaque handle (§10 — handles never serialise).</summary>
    public const string HandleInResult = "handle_in_result";

    /// <summary>The run's <c>config.backend.adapter</c> has no registered backend.</summary>
    public const string UnknownBackendAdapter = "unknown_backend_adapter";

    /// <summary>The run's <c>config.backend</c> did not resolve to a <c>{ adapter, options }</c> shape.</summary>
    public const string InvalidBackendBinding = "invalid_backend_binding";

    /// <summary>A node was structurally malformed (missing/mistyped required field) — a stand-in for Phase 3 save-time validation.</summary>
    public const string MalformedNode = "malformed_node";
}

/// <summary>
/// A terminal engine failure (§8.3), carrying a stable <see cref="Code"/> from <see cref="InterpreterErrorCodes"/>.
/// Never retried; surfaced in the §10 response as <c>failure.class = "terminal"</c>.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A code is mandatory so run-failure surfacing (§10) always has one; the codeless constructors would break that.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
internal sealed class InterpreterException : Exception
{
    /// <summary>Creates a terminal engine failure.</summary>
    /// <param name="code">A stable slug from <see cref="InterpreterErrorCodes"/>.</param>
    /// <param name="message">A human-readable description for §10 surfacing.</param>
    public InterpreterException(string code, string message)
        : base(message) => Code = code;

    /// <summary>The stable failure slug.</summary>
    public string Code { get; }
}
