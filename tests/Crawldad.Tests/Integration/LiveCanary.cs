using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Marten;

namespace Crawldad.Tests.Integration;

/// <summary>
/// Shared wiring for the Phase 4 <b>live canary</b> (CRAWLDAD_PLAN.md Phase 4 success criterion 3 + Testing strategy):
/// the one gated test that scrapes a single real enforcement record from the <b>live Accela portal</b> and validates the
/// output is a structurally valid <c>RecordScrapedV1</c>. Everything here is deliberately reusable so the gated live test
/// (<see cref="LiveCanaryTests"/>) and the zero-traffic wiring proof (<see cref="CanaryWiringTests"/>) run the
/// <b>identical</b> code path — read <c>scrape-full.json</c> verbatim, drive it through <c>POST /runs</c> on the
/// <c>"local"</c> adapter, assert <c>status:"succeeded"</c>, and validate the result SHAPE (not golden equality — live
/// data drifts). The only thing that differs between the two is the origin the <c>"local"</c> adapter talks to (the live
/// site vs. the in-process fixture site).
/// <para>
/// <b>Gating (hard requirement).</b> The live test is skipped unless <see cref="EnableVar"/><c>=1</c> AND
/// <see cref="LinkVar"/> is supplied (see <see cref="Enabled"/> / <see cref="LiveCanaryFactAttribute"/>), and it carries
/// the <c>Category=LiveCanary</c> trait so CI can also filter it out explicitly. Under a plain <c>dotnet test</c> with no
/// env vars it self-skips, so the fast loop stays free of live third-party traffic.
/// </para>
/// </summary>
internal static class LiveCanary
{
    /// <summary>The opt-in switch: the live canary runs only when this is exactly <c>"1"</c>.</summary>
    public const string EnableVar = "CRAWLDAD_LIVE_CANARY";

    /// <summary>The live enforcement-record link to scrape (a canonical Accela <c>CapDetail.aspx</c> URL). Required to run.</summary>
    public const string LinkVar = "CRAWLDAD_CANARY_LINK";

    /// <summary>The optional publish date bound to the record (the reference sources this from the search row, §B.2).</summary>
    public const string PublishDateVar = "CRAWLDAD_CANARY_PUBLISH_DATE";

    /// <summary>An optional region tag recorded on the run for the cache-locality telemetry (§9); defaults to <c>local</c>.</summary>
    public const string RegionVar = "CRAWLDAD_CANARY_REGION";

    /// <summary>The xUnit trait category the CI fast loop filters out (<c>Category!=LiveCanary</c>) as defense in depth.</summary>
    public const string Category = "LiveCanary";

    /// <summary>The skip reason surfaced when the canary is not opted in — also the one-line "how to run" reminder.</summary>
    public const string SkipReason =
        "Live canary (Category=LiveCanary): set CRAWLDAD_LIVE_CANARY=1 and CRAWLDAD_CANARY_LINK=<CapDetail.aspx url> to run. " +
        "It scrapes ONE record from the LIVE Accela portal (real network) and is never part of the fast test loop.";

    /// <summary>True only when the operator has explicitly opted in AND supplied a record link — the run gate.</summary>
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnableVar), "1", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LinkVar));

    // The RecordScrapedV1 top-level shape (the `result` object-literal of scrape-full.json / the P3 goldens, §B.2):
    // eight string scalars and seven list fields. Validated for presence + type, never for value (live data drifts).
    private static readonly string[] _stringFields =
        ["link", "recordNumber", "recordType", "projectName", "recordDate", "status", "description", "parentRecordNumber"];

    private static readonly string[] _listFields =
        ["violations", "parcels", "locations", "owners", "processingStatus", "attachments", "relatedRecords"];

    /// <summary>
    /// Builds the ordinary product host for the live canary: the real <c>"local"</c> adapter (real headless Chromium +
    /// real network) with NO clock override, so the §8.1 <b>2 s global throttle</b>, the 5 s retry delays, and the run's
    /// timestamps/<c>durationMs</c> are all real wall-clock (gentle on the live site; meaningful drift diagnostics). Its
    /// Marten schema is isolated to <c>crawldad_canary</c>. Concurrency is 1 by construction: one synchronous
    /// <c>POST /runs</c>, and the throttle serializes every non-cached request globally.
    /// </summary>
    /// <returns>The host; dispose it with <c>await using</c>.</returns>
    public static async Task<IAlbaHost> BuildLiveHostAsync()
    {
        var host = await AlbaHost.For<Program>(builder => builder.UseCrawldadTestDefaults("crawldad_canary"));
        await host.ResetAllMartenDataAsync();
        return host;
    }

    /// <summary>Builds a <c>backend</c> input for the <c>"local"</c> adapter, optionally carrying a region and/or a fixture name.</summary>
    /// <param name="adapter">The adapter id (always <c>"local"</c> for the canary).</param>
    /// <param name="region">An optional region tag (live canary) — recorded on the run.</param>
    /// <param name="fixture">An optional fixture directory (wiring proof) — selects the local fixture site's corpus.</param>
    /// <returns>The <c>backend</c> input node.</returns>
    public static JsonObject Backend(string adapter, string? region = null, string? fixture = null)
    {
        var options = new JsonObject();
        if (region is not null)
        {
            options["region"] = region;
        }

        if (fixture is not null)
        {
            options["fixture"] = fixture;
        }

        var backend = new JsonObject { ["adapter"] = adapter };
        if (options.Count > 0)
        {
            backend["options"] = options;
        }

        return backend;
    }

    /// <summary>
    /// Drives the canonical <c>scrape-full.json</c> payload <b>verbatim</b> through <c>POST /runs</c> with the supplied
    /// <c>backend</c> input and returns the (cloned) response root. Identical to the acceptance/parity suites' driver —
    /// the canary shares it so the live path is proven by the wiring proof short of the live hit.
    /// </summary>
    /// <param name="host">The host to post to.</param>
    /// <param name="backend">The <c>backend</c> input node (see <see cref="Backend"/>).</param>
    /// <param name="link">The record link bound to <c>input.link</c>.</param>
    /// <param name="publishDate">The optional publish date bound to <c>input.publishDate</c>.</param>
    /// <returns>The response body root (<c>runId</c>/<c>status</c>/<c>result</c>|<c>failure</c>/<c>stats</c>).</returns>
    public static async Task<JsonElement> RunScrapeAsync(IAlbaHost host, JsonObject backend, string link, string? publishDate)
    {
        var payload = await File.ReadAllTextAsync(Path.Combine(Runner.FixturesRoot, "Payloads", "scrape-full.json"));
        var inputs = new JsonObject
        {
            ["backend"] = backend,
            ["link"] = link,
            ["attachmentStore"] = new JsonObject { ["kind"] = "fake", ["name"] = "attachmentStore" },
        };
        if (publishDate is not null)
        {
            inputs["publishDate"] = publishDate;
        }

        var body = new JsonObject { ["payload"] = JsonNode.Parse(payload), ["inputs"] = inputs };

        var scenario = await host.Scenario(x =>
        {
            x.Post.Json(body).ToUrl("/runs");
            x.StatusCodeShouldBeOk();
        });
        return (await scenario.ReadAsJsonAsync<JsonElement>()).Clone();
    }

    /// <summary>
    /// Validates the <b>SHAPE</b> of a scrape <c>result</c> as a structurally valid <c>RecordScrapedV1</c> — every string
    /// scalar and every list field present with the right type, the <c>link</c> echoing the input, and the two fields a
    /// record is meaningless without (<c>recordNumber</c>/<c>recordType</c>) non-empty. Deliberately NOT golden equality:
    /// the live portal's data drifts, so the canary asserts structure, not values (a structural break is the drift signal).
    /// </summary>
    /// <param name="result">The run's <c>result</c> element.</param>
    /// <param name="expectedLink">The link the run was driven with (must round-trip into <c>result.link</c>).</param>
    public static void AssertValidRecordScrapedV1(JsonElement result, string expectedLink)
    {
        result.ValueKind.ShouldBe(JsonValueKind.Object);

        foreach (var key in _stringFields)
        {
            result.TryGetProperty(key, out var value).ShouldBeTrue($"result is missing required string field '{key}'");
            value.ValueKind.ShouldBe(JsonValueKind.String, $"result field '{key}' should be a string");
        }

        foreach (var key in _listFields)
        {
            result.TryGetProperty(key, out var value).ShouldBeTrue($"result is missing required list field '{key}'");
            value.ValueKind.ShouldBe(JsonValueKind.Array, $"result field '{key}' should be an array");
        }

        result.GetProperty("link").GetString().ShouldBe(expectedLink);
        result.GetProperty("recordNumber").GetString().ShouldNotBeNullOrWhiteSpace();
        result.GetProperty("recordType").GetString().ShouldNotBeNullOrWhiteSpace();
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that <b>skips itself</b> unless the live canary is explicitly opted in
/// (<see cref="LiveCanary.Enabled"/>). This is the hard gate: with no env vars set, the decorated test cannot run — it is
/// reported skipped — so <c>dotnet test</c> never sends live third-party traffic by accident.
/// </summary>
public sealed class LiveCanaryFactAttribute : FactAttribute
{
    /// <summary>Sets <see cref="FactAttribute.Skip"/> unless the operator opted in with a record link.</summary>
    public LiveCanaryFactAttribute()
    {
        if (!LiveCanary.Enabled)
        {
            Skip = LiveCanary.SkipReason;
        }
    }
}
