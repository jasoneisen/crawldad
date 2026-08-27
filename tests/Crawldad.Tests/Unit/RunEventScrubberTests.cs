using Crawldad.Api.Features.Runs;
using Crawldad.Api.Infrastructure.Security;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Unit;

/// <summary>The event-sink scrubbing chokepoint (<see cref="RunEventScrubber"/>): credential-prone fields are
/// redacted before the event is appended; events with no such field pass through unchanged.</summary>
public class RunEventScrubberTests
{
    private const string _redacted = CredentialScrubber.Redaction;
    private const string _secret = "bb_live_LEAKCANARY_event_0123456789";

    private static readonly DateTimeOffset _at = FakeClock.Fixed;
    private static readonly RunStats _stats = new(0, 1, 0, 0, 0, 0);

    private static CredentialScrubber Scrubber() => new(new StubSecretScope(_secret));

    [Fact]
    public void RunStarted_scrubs_the_payload_name_and_input_key_names()
    {
        var payloadId = Guid.NewGuid();
        var scrubbed = (RunStarted)RunEventScrubber.Scrub(
            new RunStarted($"name-{_secret}", "hash", _at, [_secret, "startDate"], payloadId, 3), Scrubber());

        scrubbed.PayloadName.ShouldBe($"name-{_redacted}");
        scrubbed.InputKeys.ShouldBe([_redacted, "startDate"]);
        scrubbed.ScriptHash.ShouldBe("hash");
        scrubbed.PayloadId.ShouldBe(payloadId);
        scrubbed.PayloadRevision.ShouldBe(3);
    }

    [Fact]
    public void RunQueued_scrubs_the_payload_name_and_input_key_names() // the queued run's opener, scrubbed like RunStarted
    {
        var payloadId = Guid.NewGuid();
        var scrubbed = (RunQueued)RunEventScrubber.Scrub(
            new RunQueued($"name-{_secret}", "hash", _at, [_secret, "startDate"], payloadId, 3), Scrubber());

        scrubbed.PayloadName.ShouldBe($"name-{_redacted}");
        scrubbed.InputKeys.ShouldBe([_redacted, "startDate"]);
        scrubbed.ScriptHash.ShouldBe("hash");
        scrubbed.PayloadId.ShouldBe(payloadId);
        scrubbed.PayloadRevision.ShouldBe(3);
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

    // ----- step-trace events -----

    [Fact]
    public void Navigated_scrubs_a_credential_bearing_url()
    {
        var scrubbed = (Navigated)RunEventScrubber.Scrub(new Navigated($"wss://host/x?token={_secret}", _at), Scrubber());

        scrubbed.Url.ShouldBe($"wss://host/x?token={_redacted}");
    }

    [Fact]
    public void Clicked_scrubs_the_selector_text()
    {
        var scrubbed = (Clicked)RunEventScrubber.Scrub(new Clicked($"[data-x='{_secret}']", _at), Scrubber());

        scrubbed.SelectorText.ShouldBe($"[data-x='{_redacted}']");
    }

    [Fact]
    public void Filled_scrubs_its_target_descriptor_defensively()
    {
        // A Filled carries `secret:<refName>` — safe by construction — but the target is scrubbed like any free text,
        // so even a ref name colliding with a registered secret cannot surface.
        var scrubbed = (Filled)RunEventScrubber.Scrub(new Filled($"secret:{_secret}", _at), Scrubber());

        scrubbed.Target.ShouldBe($"secret:{_redacted}");
    }

    [Fact]
    public void Extracted_scrubs_the_key_and_value_ref()
    {
        var scrubbed = (Extracted)RunEventScrubber.Scrub(new Extracted($"k-{_secret}", "string(3)", _at), Scrubber());

        scrubbed.Key.ShouldBe($"k-{_redacted}");
        scrubbed.ValueRef.ShouldBe("string(3)"); // a shape ref carries nothing to redact
    }

    [Fact]
    public void Downloaded_scrubs_the_blob_ref()
    {
        var scrubbed = (Downloaded)RunEventScrubber.Scrub(new Downloaded($"{_secret}.pdf", "application/pdf", 10, "abc", _at), Scrubber());

        scrubbed.BlobRef.ShouldBe($"{_redacted}.pdf");
        scrubbed.ContentType.ShouldBe("application/pdf");
    }

    [Fact]
    public void Screenshotted_scrubs_the_ref_and_the_name() // the ref is a credential-free hash, the name is author free text — both scrubbed defensively
    {
        var scrubbed = (Screenshotted)RunEventScrubber.Scrub(
            new Screenshotted($"screenshots/{_secret}.png", $"after {_secret}", 4096, _at), Scrubber());

        scrubbed.ScreenshotRef.ShouldBe($"screenshots/{_redacted}.png");
        scrubbed.Name.ShouldBe($"after {_redacted}");
        scrubbed.Size.ShouldBe(4096);
    }

    [Fact]
    public void Screenshotted_with_no_name_passes_the_null_label_through() // the null-name branch — nothing to scrub
    {
        var scrubbed = (Screenshotted)RunEventScrubber.Scrub(new Screenshotted($"screenshots/{_secret}.png", null, 8, _at), Scrubber());

        scrubbed.ScreenshotRef.ShouldBe($"screenshots/{_redacted}.png");
        scrubbed.Name.ShouldBeNull();
    }

    [Fact]
    public void Captured_scrubs_the_blob_ref() // a content-addressed hash ref, scrubbed defensively like Downloaded.BlobRef; the HTML is never in the event
    {
        var scrubbed = (Captured)RunEventScrubber.Scrub(new Captured($"{_secret}.html", 4096, "abc", _at), Scrubber());

        scrubbed.BlobRef.ShouldBe($"{_redacted}.html");
        scrubbed.Size.ShouldBe(4096);
        scrubbed.Sha256.ShouldBe("abc"); // a content hash carries nothing to redact
    }

    [Fact]
    public void SelectorMiss_scrubs_the_selector_text() // the declared selector could interpolate a credential-shaped value — scrubbed like Clicked.SelectorText
    {
        var scrubbed = (SelectorMiss)RunEventScrubber.Scrub(new SelectorMiss($"[data-token='{_secret}']", 4, _at), Scrubber());

        scrubbed.Selector.ShouldBe($"[data-token='{_redacted}']");
        scrubbed.StepIndex.ShouldBe(4);
    }

    [Fact]
    public void StepFailed_scrubs_the_error_and_keeps_the_artifact_refs()
    {
        var scrubbed = (StepFailed)RunEventScrubber.Scrub(new StepFailed(3, $"boom-{_secret}", "screenshots/abc.png", "captures/def.html", _at), Scrubber());

        scrubbed.Error.ShouldBe($"boom-{_redacted}");
        scrubbed.ScreenshotRef.ShouldBe("screenshots/abc.png"); // content-addressed ref, credential-free — the screenshot's fetch key, kept as-is
        scrubbed.CaptureRef.ShouldBe("captures/def.html");      // content-addressed ref, credential-free — kept, matching its captures[] twin
        scrubbed.Index.ShouldBe(3);
    }

    [Fact]
    public void StepFailed_scrubs_its_capture_ref_identically_to_the_captured_twin_it_links()
    {
        // The failing-page capture ref (issue #101) is duplicated on both StepFailed.CaptureRef and its Captured twin.
        // A registered secret that happens to appear in the content-addressed ref must redact the SAME way on both, or the
        // explicit failure→captures[] correlation would break. A ref containing the secret proves the scrub is applied.
        var scrubber = Scrubber();
        var forgedRef = $"captures/{_secret}beef.html"; // a (pathological) ref carrying the registered secret

        var stepFailed = (StepFailed)RunEventScrubber.Scrub(new StepFailed(0, "boom", null, forgedRef, _at), scrubber);
        var captured = (Captured)RunEventScrubber.Scrub(new Captured(forgedRef, 1, "sha", _at), scrubber);

        stepFailed.CaptureRef.ShouldBe($"captures/{_redacted}beef.html"); // scrubbed...
        stepFailed.CaptureRef.ShouldBe(captured.BlobRef);                  // ...and byte-exact with its captures[] twin
    }

    [Fact]
    public void RunSessionOpened_scrubs_the_region()
    {
        var scrubbed = (RunSessionOpened)RunEventScrubber.Scrub(new RunSessionOpened($"region-{_secret}", _at), Scrubber());

        scrubbed.Region.ShouldBe($"region-{_redacted}");
    }

    [Fact]
    public void StepStarted_and_Waited_pass_through_unchanged()
    {
        var stepStarted = new StepStarted(0, "goto", _at);
        var waited = new Waited("request", 5, _at);

        RunEventScrubber.Scrub(stepStarted, Scrubber()).ShouldBeSameAs(stepStarted);
        RunEventScrubber.Scrub(waited, Scrubber()).ShouldBeSameAs(waited);
    }
}
