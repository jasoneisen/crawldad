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
/// — this is terminal (a <c>backend_unavailable</c>), not a retryable page condition. Its message is always hand-written
/// and carries no connect URL, token, or secret: the raw provider fault (which can embed credentials in the URL) is
/// never wrapped into it. <see cref="Retryable"/> distinguishes a transient connect blip (a tunnel reconnect, a 5xx, a
/// refused/reset socket) from an auth-shaped/permanent one (a rejected key, a 4xx, an absent credential) so the
/// interpreter's <c>config.connectRetry</c> loop can retry the former and fail the latter fast — but the terminal
/// classification stays <c>backend_unavailable</c> either way once the bounded attempts are spent.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A message is mandatory so run-failure surfacing always has one; a parameterless constructor would allow messageless connect faults.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
public sealed class BrowserConnectException : Exception
{
    /// <summary>Creates a <b>non-retryable</b> connect failure carrying a mandatory, secret-free description — the
    /// default for a hand-written config/permanent fault (a missing <c>credentialRef</c>, an empty session body).</summary>
    /// <param name="message">A hand-written description with no credential material.</param>
    public BrowserConnectException(string message)
        : this(message, retryable: false)
    {
    }

    /// <summary>Creates a connect failure classified as transient (retryable) or auth-shaped/permanent (not), used by an
    /// adapter's scrubbing boundary after it has classified the raw provider fault.</summary>
    /// <param name="message">A hand-written description with no credential material.</param>
    /// <param name="retryable">Whether the underlying fault was a transient connect blip worth a bounded retry.</param>
    public BrowserConnectException(string message, bool retryable)
        : base(message) => Retryable = retryable;

    /// <summary>Whether this connect fault was transient — a network/transport/5xx blip a bounded
    /// <c>config.connectRetry</c> may re-attempt. <see langword="false"/> for an auth-shaped/permanent fault (fail fast).</summary>
    public bool Retryable { get; }
}

/// <summary>The page crashed. The retryable <c>pageCrashed</c> condition, classified per <c>onPageCrashed</c>: with
/// <c>"reopenPage"</c> (default) the interpreter closes the crashed page, opens a fresh one on the same session/context,
/// and rebinds it before the retry; with <c>"fail"</c> the reopen is skipped and the crash fails the attempt on the page
/// it crashed on — retried only when <c>pageCrashed</c> is in <c>retryOn</c> (on that same page), otherwise terminal as
/// <c>retryable-exhausted</c>. A crashed page's <c>CloseAsync</c> may also throw this same type, tolerated during reopen.
/// The real backend also maps a closed-target Playwright fault (an op that starts on an already-dead page, phrased
/// <c>"…has been closed"</c>) here, so provider-side session death classifies as <c>pageCrashed</c> rather than escaping
/// as a raw engine error.</summary>
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
