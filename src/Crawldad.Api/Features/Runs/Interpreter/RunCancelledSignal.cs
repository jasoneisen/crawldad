using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Api.Features.Runs.Interpreter;

/// <summary>The internal control-flow signal a cooperative cancel raises: thrown at a node boundary, it unwinds the
/// interpreter (disposing loop shadows) to the retry loop, which reports a <c>cancelled</c> outcome with a partial
/// result. Deliberately not a retryable/terminal fault — the retry classifier never catches it; never surfaced to callers.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A pure internal control-flow signal caught within the interpreter; the extra public constructors would be dead code the coverage gate then flags.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
internal sealed class RunCancelledSignal : Exception
{
    /// <summary>Creates the cancel signal.</summary>
    public RunCancelledSignal()
        : base("the run was cancelled between steps")
    {
    }
}
