using Crawldad.Web.Features.Drift;
using Microsoft.AspNetCore.Http;

namespace Crawldad.Tests.Unit;

/// <summary>The drift endpoint's optional per-payload alert threshold parsing: <c>?threshold=N</c> tunes how many
/// newly-missing selectors are tolerated before <c>drifted</c>. A stray/absent value never rejects a monitor's poll —
/// it reads as 0 (any new miss drifts). Exercised without a live request, like the SSE last-event-id parser.</summary>
public class PayloadDriftStatusEndpointTests
{
    private static int Parse(string? queryString)
    {
        var context = new DefaultHttpContext();
        if (queryString is not null)
        {
            context.Request.QueryString = new QueryString(queryString);
        }

        return PayloadDriftStatusEndpoint.ParseThreshold(context);
    }

    [Fact]
    public void Absent_threshold_reads_as_zero() => Parse(queryString: null).ShouldBe(0);

    [Fact]
    public void A_valid_threshold_is_read() => Parse("?threshold=3").ShouldBe(3);

    [Fact]
    public void Zero_is_a_valid_threshold() => Parse("?threshold=0").ShouldBe(0);

    [Fact]
    public void A_non_numeric_threshold_reads_as_zero() => Parse("?threshold=lots").ShouldBe(0);

    [Fact]
    public void A_negative_threshold_reads_as_zero() => Parse("?threshold=-2").ShouldBe(0);
}
