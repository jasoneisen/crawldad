using System.Globalization;
using Crawldad.Contracts.Runs;
using Crawldad.Portal.Runs;

namespace Crawldad.Tests.Portal;

/// <summary>Unit tests for <see cref="RunView"/> — the pure presentation helpers behind the runs list + run-detail pages.
/// Every branch (status → colour, duration precision, URL building, the tolerant query parsers) is exercised here so the
/// <c>.razor</c> pages stay thin and the coverage gate is met without threading each permutation through bUnit.</summary>
public class RunViewTests
{
    private static readonly Guid _runId = new("7b3ad9f2-1c4e-4a08-9f21-2c9e5d1a4f60");

    [Fact]
    public void ShortId_is_the_first_eight_hex_chars()
    {
        RunView.ShortId(_runId).ShouldBe("7b3ad9f2");
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0L, "0.00s")]
    [InlineData(640L, "0.64s")]
    [InlineData(3820L, "3.82s")]
    [InlineData(9990L, "9.99s")]
    [InlineData(10000L, "10.0s")]
    [InlineData(12400L, "12.4s")]
    public void Duration_formats_seconds_with_precision_by_magnitude(long? ms, string expected)
    {
        RunView.Duration(ms).ShouldBe(expected);
    }

    [Fact]
    public void Timestamp_normalizes_to_utc()
    {
        // 10:22 at +02:00 is 08:22 UTC.
        var instant = new DateTimeOffset(2026, 8, 27, 10, 22, 14, TimeSpan.FromHours(2));
        RunView.Timestamp(instant).ShouldBe("2026-08-27 08:22:14 UTC");
    }

    [Fact]
    public void TimestampOrDash_dashes_a_missing_instant()
    {
        RunView.TimestampOrDash(null).ShouldBe("—");
        RunView.TimestampOrDash(new DateTimeOffset(2026, 8, 27, 8, 22, 14, TimeSpan.Zero)).ShouldBe("2026-08-27 08:22:14 UTC");
    }

    [Theory]
    [InlineData(RunStatus.Succeeded, "status-green")]
    [InlineData(RunStatus.Failed, "status-red")]
    [InlineData(RunStatus.Running, "status-azure")]
    [InlineData(RunStatus.Queued, "status-yellow")]
    [InlineData(RunStatus.Cancelled, "status-secondary")]
    public void StatusCss_maps_each_disposition_to_a_tabler_colour(RunStatus status, string expected)
    {
        RunView.StatusCss(status).ShouldBe(expected);
    }

    [Fact]
    public void NavActiveCss_marks_the_matching_filter_active()
    {
        RunView.NavActiveCss(RunStatus.Failed, RunStatus.Failed).ShouldBe("active");
        RunView.NavActiveCss(RunStatus.Failed, RunStatus.Succeeded).ShouldBe("");
        RunView.NavActiveCss(null, null).ShouldBe("active");         // the "All" link, no filter applied
        RunView.NavActiveCss(null, RunStatus.Failed).ShouldBe("");
    }

    [Fact]
    public void MissesCss_warns_only_when_a_selector_missed()
    {
        RunView.MissesCss(0).ShouldBe("m");
        RunView.MissesCss(2).ShouldBe("m warn");
    }

    [Fact]
    public void StepCss_flags_only_the_failing_step()
    {
        var step = new RunTimelineStep(6, "guard", DateTimeOffset.UnixEpoch, 270);
        var other = new RunTimelineStep(2, "fill", DateTimeOffset.UnixEpoch, 90);
        var failure = new RunTimelineFailure("record_not_accessible", "blocked", new RunStepRef(6, "guard"), null, null);

        RunView.StepCss(failure, step).ShouldBe("tl-step fail");    // matches the failing index
        RunView.StepCss(failure, other).ShouldBe("tl-step ok");     // a different step
        RunView.StepCss(null, step).ShouldBe("tl-step ok");         // a run that did not fail
    }

    [Fact]
    public void ScreenshotUrl_drops_a_screenshots_prefix()
    {
        RunView.ScreenshotUrl(_runId, "9f8c21.png").ShouldBe($"/app/runs/{_runId}/screenshots/9f8c21.png");
        RunView.ScreenshotUrl(_runId, "screenshots/9f8c21.png").ShouldBe($"/app/runs/{_runId}/screenshots/9f8c21.png");
    }

    [Fact]
    public void ListUrl_with_no_state_is_the_bare_path()
    {
        RunView.ListUrl(null, null, null, null, null, null).ShouldBe("/app/runs");
    }

    [Fact]
    public void ListUrl_encodes_and_omits_absent_parameters()
    {
        var payloadId = new Guid("9a3c0000-0000-0000-0000-000000000001");
        var url = RunView.ListUrl(RunStatus.Failed, payloadId, "2026-08-01T00:00:00+00:00", null, 2, 50);

        url.ShouldStartWith("/app/runs?");
        url.ShouldContain("status=Failed");
        url.ShouldContain($"payloadId={payloadId}");
        url.ShouldContain("page=2");
        url.ShouldContain("size=50");
        url.ShouldContain($"from={Uri.EscapeDataString("2026-08-01T00:00:00+00:00")}");
        url.ShouldNotContain("to="); // the null bound is omitted
        url.ShouldNotContain("+00:00"); // the '+' was percent-encoded
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Failed", RunStatus.Failed)]
    [InlineData("failed", RunStatus.Failed)]    // case-insensitive
    [InlineData("3", null)]                      // an ordinal is rejected, exactly like the API
    [InlineData("bogus", null)]
    public void ParseStatus_is_tolerant(string? raw, RunStatus? expected)
    {
        RunView.ParseStatus(raw).ShouldBe(expected);
    }

    [Fact]
    public void ParsePayloadId_parses_a_uuid_or_null()
    {
        var id = Guid.NewGuid();
        RunView.ParsePayloadId(id.ToString()).ShouldBe(id);
        RunView.ParsePayloadId("not-a-guid").ShouldBeNull();
        RunView.ParsePayloadId(null).ShouldBeNull();
    }

    [Fact]
    public void ParseInstant_parses_iso_or_null()
    {
        RunView.ParseInstant("2026-08-01T00:00:00+00:00").ShouldBe(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        RunView.ParseInstant("nonsense").ShouldBeNull();
        RunView.ParseInstant(null).ShouldBeNull();
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("abc", 1)]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("2", 2)]
    public void ParsePage_floors_at_one(string? raw, int expected)
    {
        RunView.ParsePage(raw).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("abc", null)]
    [InlineData("0", 1)]     // clamped up
    [InlineData("50", 50)]
    [InlineData("200", 100)] // clamped down
    public void ParseSize_clamps_or_defaults(string? raw, int? expected)
    {
        RunView.ParseSize(raw).ShouldBe(expected);
    }

    [Fact]
    public void HasFilter_is_true_when_any_bound_is_set()
    {
        RunView.HasFilter(null, null, null, null).ShouldBeFalse();
        RunView.HasFilter(RunStatus.Failed, null, null, null).ShouldBeTrue();
        RunView.HasFilter(null, Guid.NewGuid(), null, null).ShouldBeTrue();
        RunView.HasFilter(null, null, DateTimeOffset.UnixEpoch, null).ShouldBeTrue();
        RunView.HasFilter(null, null, null, DateTimeOffset.UnixEpoch).ShouldBeTrue();
    }

    [Fact]
    public void PayloadLabel_reads_inline_pinned_and_revisionless()
    {
        var payloadId = Guid.NewGuid();
        RunView.PayloadLabel(null, "demo", null).ShouldBe("demo · inline");
        RunView.PayloadLabel(payloadId, "permits.search", 3).ShouldBe("permits.search · r3");
        RunView.PayloadLabel(payloadId, "permits.search", null).ShouldBe("permits.search");
    }

    // Guards against a locale-sensitive regression in the numeric formatting (the helpers pin InvariantCulture).
    [Fact]
    public void Formatting_is_culture_invariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // comma decimal separator
            RunView.Duration(3820).ShouldBe("3.82s");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
