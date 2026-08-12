using System.Diagnostics.CodeAnalysis;

namespace Crawldad.Web.Infrastructure.Browser.Fake;

/// <summary>A tenant fixture <b>replay</b> diverged from what the set recorded: the payload navigated to a URL the set
/// never captured (<c>fixture_state_miss</c>), or clicked an element with no recorded transition
/// (<c>fixture_transition_miss</c>). Raised only under a strict (tenant) manifest — the internal fixtures keep the
/// lenient fallback. Terminal and secret-free by construction, it names the miss so a divergence fails classified rather
/// than hanging or silently mis-replaying. Deliberately not a <see cref="BrowserException"/> (which is the retryable-page
/// base): the interpreter classifies it terminal at its own catch, alongside the fake's setup fault.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A code + message are mandatory so the divergence is always self-describing and classified; a parameterless constructor would allow a codeless miss.")]
[SuppressMessage("Roslynator", "RCS1194:Implement exception constructors",
    Justification = "See CA1032 justification.")]
public sealed class FixtureDivergenceException : Exception
{
    /// <summary>The terminal failure code for a navigation to a URL the fixture set never recorded.</summary>
    public const string StateMissCode = "fixture_state_miss";

    /// <summary>The terminal failure code for a click with no recorded transition from the current state.</summary>
    public const string TransitionMissCode = "fixture_transition_miss";

    /// <summary>Creates a divergence failure carrying its stable <paramref name="code"/> and a naming description.</summary>
    /// <param name="code">The terminal failure slug (<see cref="StateMissCode"/> / <see cref="TransitionMissCode"/>).</param>
    /// <param name="message">What diverged (the unrecorded URL, or the state whose click had no transition).</param>
    public FixtureDivergenceException(string code, string message)
        : base(message) => Code = code;

    /// <summary>The stable failure slug surfaced as <c>failure.code</c>.</summary>
    public string Code { get; }
}
