using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Crawldad.Tests.Support;
using Marten;

namespace Crawldad.Tests.Integration;

/// <summary>Shared wiring for the live canary: the one gated test that scrapes a single real record from the live
/// Accela portal and validates <c>RecordScrapedV1</c> shape. <see cref="LiveCanaryTests"/> and
/// <see cref="CanaryWiringTests"/> share this identical code path; skipped unless <see cref="EnableVar"/>=1 and <see cref="LinkVar"/> are set.</summary>
internal static class LiveCanary
{
    /// <summary>The opt-in switch: the live canary runs only when this is exactly <c>"1"</c>.</summary>
    public const string EnableVar = "CRAWLDAD_LIVE_CANARY";

    /// <summary>The live enforcement-record link to scrape (a canonical Accela <c>CapDetail.aspx</c> URL). Required to run.</summary>
    public const string LinkVar = "CRAWLDAD_CANARY_LINK";

    /// <summary>The optional publish date bound to the record (normally sourced from the search row).</summary>
    public const string PublishDateVar = "CRAWLDAD_CANARY_PUBLISH_DATE";

    /// <summary>An optional region tag recorded on the run for cache-locality telemetry; defaults to <c>local</c>.</summary>
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

    // The RecordScrapedV1 top-level shape (the `result` object-literal of scrape-full.json): eight string scalars and
    // seven list fields. Validated for presence + type, never for value (live data drifts).
    private static readonly string[] _stringFields =
        ["link", "recordNumber", "recordType", "projectName", "recordDate", "status", "description", "parentRecordNumber"];

    private static readonly string[] _listFields =
        ["violations", "parcels", "locations", "owners", "processingStatus", "attachments", "relatedRecords"];

    /// <summary>Builds the ordinary product host for the live canary: the real <c>"local"</c> adapter (real headless
    /// Chromium + real network) with NO clock override, so the throttle, retry delays, and the run's timestamps are
    /// all real wall-clock. Marten schema <c>crawldad_canary</c>; concurrency is 1 by construction.</summary>
    public static async Task<IAlbaHost> BuildLiveHostAsync()
    {
        var host = (await AlbaHost.For<Program>(builder => builder.UseCrawldadTestDefaults("crawldad_canary")))
            .AuthenticatedAsPrimaryTenant();
        await host.ResetAllMartenDataAsync();
        return host;
    }

    /// <summary>Builds a <c>backend</c> input for the <c>"local"</c> adapter, optionally carrying a region and/or a fixture name.</summary>
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

    /// <summary>Drives <c>scrape-full.json</c> verbatim through <c>POST /runs</c> with the supplied <c>backend</c>
    /// input and returns the cloned response root (<c>runId</c>/<c>status</c>/<c>result</c>|<c>failure</c>/<c>stats</c>).
    /// Identical to the acceptance/parity suites' driver, shared so the live path is proven by the wiring proof too.</summary>
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

    /// <summary>Validates the SHAPE of a scrape <c>result</c> as a structurally valid <c>RecordScrapedV1</c> — every
    /// field present with the right type, <c>link</c> echoing the input, and <c>recordNumber</c>/<c>recordType</c>
    /// non-empty. Deliberately not golden equality: live data drifts, so a structural break is the drift signal.</summary>
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

/// <summary>A <see cref="FactAttribute"/> that skips itself unless the live canary is explicitly opted in
/// (<see cref="LiveCanary.Enabled"/>). This is the hard gate: with no env vars set, the decorated test is reported
/// skipped, so <c>dotnet test</c> never sends live third-party traffic by accident.</summary>
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
