using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Web.Features.Runs.Interpreter;

/// <summary>
/// The internal control-flow signal a cooperative cancel raises (§11): thrown at a node boundary when the run observer
/// reports a cancel was requested, it unwinds the interpreter (disposing loop shadows) to the retry loop, which reports a
/// <c>cancelled</c> outcome with a partial result — the backend session then tears down cleanly via the run's
/// <c>await using</c>. It is deliberately <b>not</b> one of the retryable/terminal engine faults, so the retry classifier
/// never catches it; it is handled by its own dedicated catch. Never surfaced to a caller as an exception.
/// </summary>
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
