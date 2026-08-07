using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Tests.Unit;

/// <summary>
/// The SSE frame codec + reconnect parsing (§11): frames carry the stream <c>version</c> as the <c>id</c> (so
/// <c>Last-Event-ID</c> resumes exactly) with camelCase JSON data; the terminal events close a tail; and the last-seen
/// sequence is read from the <c>Last-Event-ID</c> header, then a <c>lastEventId</c> query param, else 0.
/// </summary>
public class RunEventFramesTests
{
    [Fact]
    public void Format_writes_id_event_and_camel_case_json_data()
    {
        var frame = RunEventFrames.Format(7, "StepStarted", new StepStarted(2, "loop", FakeClock.Fixed));

        frame.ShouldStartWith("id: 7\nevent: StepStarted\ndata: {");
        frame.ShouldContain("\"index\":2");     // camelCase, value present
        frame.ShouldContain("\"kind\":\"loop\"");
        frame.ShouldEndWith("\n\n");            // blank-line terminated
    }

    [Fact]
    public void IsTerminal_is_true_only_for_the_terminal_events()
    {
        RunEventFrames.IsTerminal(typeof(RunSucceeded)).ShouldBeTrue();
        RunEventFrames.IsTerminal(typeof(RunFailed)).ShouldBeTrue();
        RunEventFrames.IsTerminal(typeof(RunCancelled)).ShouldBeTrue();
        RunEventFrames.IsTerminal(typeof(StepStarted)).ShouldBeFalse();
    }

    [Fact]
    public void ParseLastEventId_prefers_the_header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Last-Event-ID"] = "12";
        context.Request.QueryString = new QueryString("?lastEventId=99"); // header wins over the query

        RunEventStream.ParseLastEventId(context).ShouldBe(12);
    }

    [Fact]
    public void ParseLastEventId_falls_back_to_the_query_param()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Last-Event-ID"] = "not-a-number"; // ignored — falls through to the query
        context.Request.QueryString = new QueryString("?lastEventId=34");

        RunEventStream.ParseLastEventId(context).ShouldBe(34);
    }

    [Fact]
    public void ParseLastEventId_defaults_to_zero_when_absent_or_unparseable()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?lastEventId=nope"); // unparseable query, no header

        RunEventStream.ParseLastEventId(context).ShouldBe(0);
    }
}
