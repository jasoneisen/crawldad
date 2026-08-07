using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Crawldad.Web.Infrastructure.Security;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The event-sink scrubbing chokepoint (<see cref="RunEventScrubber"/>, §12, WP3): credential-prone fields of each
/// trace event are redacted before the event is appended, and events with no such field pass through unchanged.
/// </summary>
public class RunEventScrubberTests
{
    private const string _redacted = CredentialScrubber.Redaction;
    private const string _secret = "bb_live_LEAKCANARY_event_0123456789";

    private static readonly DateTimeOffset _at = FakeClock.Fixed;
    private static readonly RunStats _stats = new(0, 1, 0, 0, 0);

    private static CredentialScrubber Scrubber() => new(new StubSecretScope(_secret));

    [Fact]
    public void RunStarted_scrubs_the_payload_name_and_input_key_names()
    {
        var scrubbed = (RunStarted)RunEventScrubber.Scrub(
            new RunStarted($"name-{_secret}", "hash", _at, [_secret, "startDate"]), Scrubber());

        scrubbed.PayloadName.ShouldBe($"name-{_redacted}");
        scrubbed.InputKeys.ShouldBe([_redacted, "startDate"]);
        scrubbed.ScriptHash.ShouldBe("hash"); // untouched
    }

    [Fact]
    public void LogEmitted_scrubs_the_message()
    {
        var scrubbed = (LogEmitted)RunEventScrubber.Scrub(
            new LogEmitted("info", $"the page echoed {_secret}", _at), Scrubber());

        scrubbed.Message.ShouldBe($"the page echoed {_redacted}");
        scrubbed.Level.ShouldBe("info");
    }

    [Fact]
    public void RunAttemptFailed_passes_through_unchanged()
    {
        var original = new RunAttemptFailed(1, "timeout", _at);

        RunEventScrubber.Scrub(original, Scrubber()).ShouldBeSameAs(original);
    }

    [Fact]
    public void RunSucceeded_passes_through_unchanged()
    {
        var original = new RunSucceeded(_stats, _at);

        RunEventScrubber.Scrub(original, Scrubber()).ShouldBeSameAs(original);
    }

    [Fact]
    public void ScrubFailure_scrubs_the_message_only()
    {
        var scrubbed = RunEventScrubber.ScrubFailure(
            new RunFailureDetail("terminal", "backend_unavailable", $"connect failed for token={_secret}", new RunStepRef(2, "config")),
            Scrubber());

        scrubbed.Message.ShouldBe($"connect failed for token={_redacted}");
        scrubbed.Code.ShouldBe("backend_unavailable");
        scrubbed.Class.ShouldBe("terminal");
        scrubbed.AtStep.Index.ShouldBe(2);
    }
}
