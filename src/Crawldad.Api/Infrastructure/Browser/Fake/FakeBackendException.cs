using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Api.Infrastructure.Browser.Fake;

/// <summary>A configuration fault in the record/replay fake: a missing fixture directory, absent manifest.json, or a
/// malformed manifest. Distinct from a scripted <see cref="BrowserTimeoutException"/> — this is a setup error,
/// classified terminal by the interpreter (not a retryable page condition).</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A message is mandatory so the fault is always self-describing; a parameterless constructor would allow messageless config faults.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
public sealed class FakeBackendException : Exception
{
    /// <summary>Creates a fake-backend configuration fault.</summary>
    /// <param name="message">What was missing or malformed.</param>
    public FakeBackendException(string message)
        : base(message)
    {
    }
}
