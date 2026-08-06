using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>
/// A typed failure raised by a <c>guard</c> (its <c>cond</c> did not hold) or a <c>fail</c> node (§4/§6 <c>Failure</c>
/// payload). Carries the payload-declared <see cref="FailureClass"/> (<c>terminal</c> or <c>retryable</c>), a stable
/// <see cref="Code"/>, and an already-rendered <see cref="Exception.Message"/> (its <c>${…}</c> interpolations resolved
/// at raise time). The retry classifier (§8.3) reads <see cref="IsRetryable"/>: a <c>retryable</c> fail participates in
/// retry like a timeout; a <c>terminal</c> one is never retried.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "Class/code/message are all mandatory so run-failure surfacing (§10) is always complete; the codeless constructors would break that.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
internal sealed class CrawldadFailureException : Exception
{
    /// <summary>Creates a typed failure from a <c>guard</c>/<c>fail</c> <c>Failure</c> payload.</summary>
    /// <param name="failureClass">The declared class: <c>terminal</c> or <c>retryable</c>.</param>
    /// <param name="code">The stable failure slug.</param>
    /// <param name="message">The rendered human-readable message.</param>
    public CrawldadFailureException(string failureClass, string code, string message)
        : base(message)
    {
        FailureClass = failureClass;
        Code = code;
    }

    /// <summary>The declared failure class (<c>terminal</c> or <c>retryable</c>).</summary>
    public string FailureClass { get; }

    /// <summary>The stable failure slug (surfaced as <c>failure.code</c>, §10).</summary>
    public string Code { get; }

    /// <summary>Whether this failure participates in retry (§8.3): true iff the declared class is <c>retryable</c>.</summary>
    public bool IsRetryable => string.Equals(FailureClass, "retryable", StringComparison.Ordinal);
}
