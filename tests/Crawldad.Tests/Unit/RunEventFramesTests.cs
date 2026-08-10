using Crawldad.Contracts.Runs;
using Crawldad.Tests.Support;
using Crawldad.Web.Features.Runs;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Tests.Unit;

/// <summary>SSE frame codec + reconnect parsing: the stream <c>version</c> is the frame <c>id</c> so
/// <c>Last-Event-ID</c> resumes exactly; last-seen sequence reads the header, then a query param, else 0.</summary>
public class RunEventFramesTests
{
    [Fact]
    public void Format_writes_id_event_and_camel_case_json_data()
    {
        var frame = RunEventFrames.Format(7, "StepStarted", new StepStarted(2, "loop", FakeClock.Fixed));

        frame.ShouldStartWith("id: 7\nevent: StepStarted\ndata: {");
        frame.ShouldContain("\"index\":2");
        frame.ShouldContain("\"kind\":\"loop\"");
        frame.ShouldEndWith("\n\n");
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
        context.Request.QueryString = new QueryString("?lastEventId=99");

        RunEventStream.ParseLastEventId(context).ShouldBe(12);
    }

    [Fact]
    public void ParseLastEventId_falls_back_to_the_query_param()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Last-Event-ID"] = "not-a-number";
        context.Request.QueryString = new QueryString("?lastEventId=34");

        RunEventStream.ParseLastEventId(context).ShouldBe(34);
    }

    [Fact]
    public void ParseLastEventId_defaults_to_zero_when_absent_or_unparseable()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?lastEventId=nope");

        RunEventStream.ParseLastEventId(context).ShouldBe(0);
    }
}
