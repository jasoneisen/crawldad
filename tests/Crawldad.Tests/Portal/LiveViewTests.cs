using Crawldad.Contracts.Runs;
using Crawldad.Portal.Live;

namespace Crawldad.Tests.Portal;

/// <summary>Unit tests for the pure live-trace presentation helpers: the event → feed-row colour class, the running
/// status a frame implies, the completion headline, and the data-cell preview elision.</summary>
public class LiveViewTests
{
    [Theory]
    [InlineData("Navigated", "evt e-nav")]
    [InlineData("Clicked", "evt e-click")]
    [InlineData("Waited", "evt e-wait")]
    [InlineData("Extracted", "evt e-extract")]
    [InlineData("Filled", "evt e-fill")]
    [InlineData("SelectorMiss", "evt e-miss")]
    [InlineData("Screenshotted", "evt e-shot")]
    [InlineData("Captured", "evt e-cap")]
    [InlineData("LogEmitted", "evt e-log")]
    [InlineData("StepFailed", "evt e-fail")]
    [InlineData("RunFailed", "evt e-fail")]
    [InlineData("StepStarted", "evt e-step")]
    [InlineData("RunStarted", "evt e-step")] // an unmapped lifecycle event falls to the neutral step tone
    public void EventCss_maps_each_event_family_to_its_row_class(string eventType, string expected) =>
        LiveView.EventCss(eventType).ShouldBe(expected);

    [Theory]
    [InlineData(RunStatus.Running, "RunQueued", RunStatus.Queued)]
    [InlineData(RunStatus.Queued, "RunStarted", RunStatus.Running)]
    [InlineData(RunStatus.Queued, "RunDequeued", RunStatus.Running)]
    [InlineData(RunStatus.Running, "RunResumed", RunStatus.Running)]
    [InlineData(RunStatus.Queued, "StepStarted", RunStatus.Running)]
    [InlineData(RunStatus.Running, "RunSucceeded", RunStatus.Succeeded)]
    [InlineData(RunStatus.Running, "RunFailed", RunStatus.Failed)]
    [InlineData(RunStatus.Running, "RunCancelled", RunStatus.Cancelled)]
    [InlineData(RunStatus.Running, "Navigated", RunStatus.Running)] // a non-status event leaves the status unchanged
    public void StatusFor_folds_a_frame_onto_the_current_status(RunStatus current, string eventType, RunStatus expected) =>
        LiveView.StatusFor(current, eventType).ShouldBe(expected);

    [Theory]
    [InlineData(RunStatus.Succeeded, "Run succeeded")]
    [InlineData(RunStatus.Failed, "Run failed")]
    [InlineData(RunStatus.Cancelled, "Run cancelled")]
    [InlineData(RunStatus.Running, "Run finished")] // still-open dispositions fall to the neutral label
    public void CompletionLabel_headlines_the_terminal_disposition(RunStatus status, string expected) =>
        LiveView.CompletionLabel(status).ShouldBe(expected);

    [Fact]
    public void Preview_trims_and_passes_through_a_short_body() =>
        LiveView.Preview("  {\"a\":1}  ").ShouldBe("{\"a\":1}");

    [Fact]
    public void Preview_elides_a_body_past_the_cap()
    {
        var preview = LiveView.Preview(new string('x', LiveView.MaxPreview + 40));

        preview.Length.ShouldBe(LiveView.MaxPreview + 1); // the cap plus the single ellipsis
        preview.ShouldEndWith("…");
        preview.ShouldStartWith("xxxx");
    }
}
