using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>
/// The base for the two <b>retryable</b> browser conditions in the §8.3 taxonomy (<see cref="BrowserTimeoutException"/>
/// and <see cref="BrowserPageCrashedException"/>). Real Phase 4 adapters map Playwright's <c>TimeoutException</c> and
/// its <c>"Page crashed"</c> <c>PlaywrightException</c> onto these two types so the interpreter classifies uniformly;
/// the record/replay fake throws them from scripted <c>inject</c> faults. Having a common base lets the reopen path
/// tolerate a crashed page's close failure with a single, specific catch (never a blanket <c>catch (Exception)</c>).
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A message is mandatory so run-failure surfacing (§10) always has one; a parameterless constructor would allow messageless browser faults.")]
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

/// <summary>
/// A backend operation timed out waiting for the page (a <c>waitForRequest</c> whose request never fired, a
/// <c>waitFor</c> whose element never reached its state). The retryable <c>timeout</c> condition in the §8.3 taxonomy;
/// real Phase 4 adapters throw the same type so the interpreter classifies uniformly. Retried per <c>config.retry</c>
/// when <c>"timeout"</c> is in <c>retryOn</c>; exhausting the attempts yields <c>retryable-exhausted</c>.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A message is mandatory so run-failure surfacing (§10) always has one; a parameterless constructor would allow messageless timeouts.")]
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

/// <summary>
/// A real adapter could not establish its backend session — a missing/unresolvable credential, or the underlying
/// <c>connect</c>/<c>connectOverCDP</c> failing. Deliberately <b>not</b> a <see cref="BrowserException"/> (those are
/// retryable page conditions): a connect failure is terminal (<c>backend_unavailable</c>, §8.3), classified by the
/// interpreter's setup catch like the fake's <c>FakeBackendException</c>. Its message is <b>always</b> hand-written and
/// carries <b>no</b> connect URL, token, or resolved secret (§12) — the raw provider fault (which can embed the
/// credential in a <c>wss://…?token=</c> URL) is never wrapped into it.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A message is mandatory so run-failure surfacing (§10) always has one; a parameterless constructor would allow messageless connect faults.")]
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

/// <summary>
/// The page crashed (the reference's <c>PlaywrightException</c> whose message starts <c>"Page crashed"</c>, §3.6). The
/// retryable <c>pageCrashed</c> condition in the §8.3 taxonomy: when retried with <c>onPageCrashed: "reopenPage"</c>
/// the interpreter closes the crashed page, opens a fresh one on the <b>same</b> session/context, and rebinds it
/// (fixing the reference's latent bug where the reopened page was assigned to a lost local). A crashed page may also
/// fail to close cleanly, so this same type is thrown by a crashed page's <c>CloseAsync</c> and tolerated during reopen.
/// </summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A message is mandatory so run-failure surfacing (§10) always has one; a parameterless constructor would allow messageless crashes.")]
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
