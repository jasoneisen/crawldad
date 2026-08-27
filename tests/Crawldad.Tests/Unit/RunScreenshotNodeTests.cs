using Crawldad.Api.Features.Runs;
using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;

namespace Crawldad.Tests.Unit;

/// <summary>The explicit <c>screenshot</c> node: the author-authored analogue of screenshot-on-failure, flowing through
/// the same <c>IScreenshotStore</c> seam — the <c>Screenshotted</c> event carries only the content-addressed ref + byte
/// size + optional label, never the image. Inert on the synchronous path. Driven via <see cref="Runner"/> against the fake.</summary>
public class RunScreenshotNodeTests
{
    // Navigate a real page first (so a page is bound), then screenshot it. `{{screenshotBody}}` is the only interpolation —
    // a literal ${…} inside it survives to the payload for the template parser to render at run time.
    private static string Payload(string screenshotBody) =>
        $$"""
        { "name": "t", "config": { "backend": "input.backend" }, "vars": {},
          "steps": [ { "goto": { "url": "https://aca-prod.accela.com/LJCMG/Cap/CapHome.aspx" } },
                     { "screenshot": {{screenshotBody}} } ],
          "result": "'ok'" }
        """;

    [Fact]
    public async Task A_screenshot_node_captures_the_page_and_records_a_ref_with_metadata()
    {
        // The name is a Tmpl expression, interpolating an input like sibling nodes.
        var (outcome, observer, screenshots) = await Runner.RunWithObserverAsync(
            Payload("""{ "name": "after-${input.tag}" }"""),
            """{ "backend": { "adapter": "fake", "options": { "fixture": "caphome-search" } }, "tag": "load" }""");

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);

        var shot = observer.Events.OfType<Screenshotted>().ShouldHaveSingleItem();
        shot.Name.ShouldBe("after-load");
        shot.ScreenshotRef.ShouldStartWith("screenshots/"); // content-addressed, credential-free

        // The store holds the captured PNG bytes at the ref, and the event's Size is that byte count (metadata only).
        var bytes = screenshots.Blobs[shot.ScreenshotRef];
        bytes[..8].ShouldBe([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // PNG signature
        shot.Size.ShouldBe(bytes.Length);
        shot.Size.ShouldBeGreaterThan(8); // a real (fake) capture: the 8-byte signature plus the page identity
    }

    [Fact]
    public async Task A_screenshot_node_without_a_name_records_a_null_label()
    {
        var (outcome, observer, screenshots) = await Runner.RunWithObserverAsync(Payload("{}"));

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        var shot = observer.Events.OfType<Screenshotted>().ShouldHaveSingleItem();
        shot.Name.ShouldBeNull();
        screenshots.Blobs.Keys.ShouldContain(shot.ScreenshotRef);
    }

    [Fact]
    public async Task The_synchronous_path_captures_nothing()
    {
        // No observer + no screenshot store ⇒ the node is inert (no capture, no store, no event), so the sync goldens
        // are byte-identical. The run still succeeds; nothing accrues for the endpoint to append.
        var (outcome, _) = await Runner.RunWithFakeAsync(Payload("""{ "name": "unused" }"""));

        outcome.Status.ShouldBe(RunStatus.Succeeded, outcome.Failure?.Code);
        outcome.Events.OfType<Screenshotted>().ShouldBeEmpty();
    }
}
