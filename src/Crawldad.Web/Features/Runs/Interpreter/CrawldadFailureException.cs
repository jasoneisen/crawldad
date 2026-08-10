using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>A typed failure raised by a <c>guard</c> whose <c>cond</c> failed, or a <c>fail</c> node. Carries the
/// declared <see cref="FailureClass"/> (<c>terminal</c>/<c>retryable</c>), a stable <see cref="Code"/>, and a rendered
/// message. A <c>retryable</c> failure participates in retry like a timeout; <c>terminal</c> never retries.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "Class/code/message are all mandatory so run-failure surfacing is always complete; the codeless constructors would break that.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
internal sealed class CrawldadFailureException : Exception
{
    /// <summary>Creates a typed failure from a <c>guard</c>/<c>fail</c> <c>Failure</c> payload.</summary>
    public CrawldadFailureException(string failureClass, string code, string message)
        : base(message)
    {
        FailureClass = failureClass;
        Code = code;
    }

    /// <summary>The declared failure class (<c>terminal</c> or <c>retryable</c>).</summary>
    public string FailureClass { get; }

    /// <summary>The stable failure slug (surfaced as <c>failure.code</c>).</summary>
    public string Code { get; }

    /// <summary>Whether this failure participates in retry: true iff the declared class is <c>retryable</c>.</summary>
    public bool IsRetryable => string.Equals(FailureClass, "retryable", StringComparison.Ordinal);
}
