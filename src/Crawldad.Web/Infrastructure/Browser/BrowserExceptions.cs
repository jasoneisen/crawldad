using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>
/// A backend operation timed out waiting for the page (a <c>waitForRequest</c> whose request never fired, a
/// <c>waitFor</c> whose element never reached its state). This is the retryable class in the §8.3 taxonomy; real
/// Phase 4 adapters throw the same type so the interpreter classifies uniformly. In Phase 1 there is a single
/// attempt, so a timeout surfaces as <c>retryable-exhausted</c> (the retry loop is Phase 2).
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A message is mandatory so run-failure surfacing (§10) always has one; a parameterless constructor would allow messageless timeouts.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
public sealed class BrowserTimeoutException : Exception
{
    /// <summary>Creates a timeout failure.</summary>
    /// <param name="message">What was being awaited when the timeout fired.</param>
    public BrowserTimeoutException(string message)
        : base(message)
    {
    }
}
