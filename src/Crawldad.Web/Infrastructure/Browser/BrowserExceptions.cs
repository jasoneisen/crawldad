using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>The base for the two <b>retryable</b> browser conditions (<see cref="BrowserTimeoutException"/> and
/// <see cref="BrowserPageCrashedException"/>). A common base lets the reopen path tolerate a crashed page's close
/// failure with one specific catch — never a blanket <c>catch (Exception)</c>.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A message is mandatory so run-failure surfacing always has one; a parameterless constructor would allow messageless browser faults.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
public abstract class BrowserException : Exception
{
    /// <summary>Creates a browser fault carrying a mandatory description.</summary>
    /// <param name="message">What the backend was doing when the fault fired.</param>
    protected BrowserException(string message)
        : base(message)
    {
    }
}

/// <summary>A backend operation timed out waiting for the page (e.g. a <c>waitForRequest</c> that never fired, or a
/// <c>waitFor</c> whose element never reached its state). Retried per <c>config.retry</c> when <c>"timeout"</c> is in
/// <c>retryOn</c>; exhausting the attempts yields <c>retryable-exhausted</c>.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A message is mandatory so run-failure surfacing always has one; a parameterless constructor would allow messageless timeouts.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
public sealed class BrowserTimeoutException : BrowserException
{
    /// <summary>Creates a timeout failure.</summary>
    /// <param name="message">What was being awaited when the timeout fired.</param>
    public BrowserTimeoutException(string message)
        : base(message)
    {
    }
}

/// <summary>A real adapter could not establish its backend session. Deliberately not a <see cref="BrowserException"/>
/// — this is terminal, not a retryable page condition. Its message is always hand-written and carries no connect URL,
/// token, or secret: the raw provider fault (which can embed credentials in the URL) is never wrapped into it.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A message is mandatory so run-failure surfacing always has one; a parameterless constructor would allow messageless connect faults.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
public sealed class BrowserConnectException : Exception
{
    /// <summary>Creates a connect failure carrying a mandatory, secret-free description.</summary>
    /// <param name="message">A hand-written description with no credential material.</param>
    public BrowserConnectException(string message)
        : base(message)
    {
    }
}

/// <summary>The page crashed. The retryable <c>pageCrashed</c> condition: with <c>onPageCrashed: "reopenPage"</c> the
/// interpreter closes the crashed page, opens a fresh one on the same session/context, and rebinds it. A crashed
/// page's <c>CloseAsync</c> may also throw this same type, tolerated during reopen.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A message is mandatory so run-failure surfacing always has one; a parameterless constructor would allow messageless crashes.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
public sealed class BrowserPageCrashedException : BrowserException
{
    /// <summary>Creates a page-crash failure.</summary>
    /// <param name="message">What the page was doing when it crashed.</param>
    public BrowserPageCrashedException(string message)
        : base(message)
    {
    }
}
