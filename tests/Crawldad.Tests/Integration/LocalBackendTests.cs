using System.Collections.Generic;
using System.IO;
using Crawldad.Tests.Support;
using Crawldad.Web.Infrastructure.Browser;

namespace Crawldad.Tests.Integration;

/// <summary>The shared Playwright wrapper and the <c>"local"</c> adapter, driven against a real headless Chromium
/// over a loopback origin. Covers goto/waits/locators/frames/reads/actions/download through the seam, the route
/// policy (block/cache/pass-through), the region tag, and context-only teardown.</summary>
[Collection(RealChromiumCollection.Name)]
[Trait("Category", RealChromiumCollection.Category)]
public sealed class LocalBackendTests(RealChromiumFixture fixture)
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    private const string _pageHtml =
        """
        <html><head><title>t</title><link rel="stylesheet" href="/style.css"></head>
        <body>
          <h1 id="title">Hello &amp; World</h1>
          <div id="box" data-kind="widget"><span>inner</span></div>
          <input id="name" value="init">
          <ul id="list"><li>alpha</li><li>bravo</li><li>charlie</li></ul>
          <a id="go" href="/next.html">next</a>
          <button title="Search records" id="btn" onclick="fetch('/other'); fetch('/api', { method: 'POST' });">Search</button>
          <iframe id="fr" src="/frame.html"></iframe>
          <div id="overlay" style="display:none">hidden</div>
        </body></html>
        """;

    private static BackendBinding Binding(string? region = null) =>
        new("local", null, region is null ? null : new Dictionary<string, object?>(StringComparer.Ordinal) { ["region"] = region });

    private static LocalSite BasicSite() => new LocalSite()
        .Map("/page.html", "text/html", _pageHtml)
        .Map("/next.html", "text/html", "<html><body><h1 id='done'>Done</h1></body></html>")
        .Map("/frame.html", "text/html", "<html><body><p id='fp'>frame-content</p></body></html>")
        .Map("/style.css", "text/css", "body{color:red}")
        .Map("/api", "text/plain", "ok")
        .Map("/other", "text/plain", "other"); // a non-matching request so the waitForRequest predicate sees a URL miss

    [Fact]
    public async Task Drives_reads_locators_frames_and_actions()
    {
        using var site = BasicSite();
        await using var session = await fixture.LocalBackend.ConnectAsync(Binding(), SessionPolicy.Default, _ct);
        var page = await session.NewPageAsync(_ct);
        await page.GotoAsync(site.Url("/page.html"), "load", null, _ct);

        page.Url.ShouldBe(site.Url("/page.html"));

        // reads (entity-decoded textContent, attribute, innerText/innerHTML)
        (await page.Locator("#title").TextContentAsync(_ct)).ShouldBe("Hello & World");
        (await page.Locator("#box").GetAttributeAsync("data-kind", _ct)).ShouldBe("widget");
        (await page.Locator("#box").GetAttributeAsync("data-missing", _ct)).ShouldBeNull(); // attribute absent
        (await page.Locator("#box").InnerHTMLAsync(_ct)).ShouldBe("<span>inner</span>");
        (await page.Locator("#box").InnerTextAsync(_ct)).ShouldBe("inner");

        // locators: count, child, nth/first, filter, GetByTitle
        (await page.Locator("#list li").CountAsync(_ct)).ShouldBe(3);
        (await page.Locator("#list").Locator("li").CountAsync(_ct)).ShouldBe(3);
        (await page.Locator("#list li").Nth(1).TextContentAsync(_ct)).ShouldBe("bravo");
        (await page.Locator("#list li").First.TextContentAsync(_ct)).ShouldBe("alpha");
        (await page.Locator("#list li").Filter("bravo").CountAsync(_ct)).ShouldBe(1);
        (await page.GetByTitle("Search records").CountAsync(_ct)).ShouldBe(1);

        // frame: read inside the iframe's document
        (await page.FrameLocator("#fr").Locator("#fp").TextContentAsync(_ct)).ShouldBe("frame-content");

        // actions: addStyleTag, fill/clear, waitFor visible + hidden (fill/clear set the value PROPERTY, not the
        // attribute — a fake-vs-real divergence; here we only assert the actions run cleanly)
        await page.AddStyleTagAsync("body{background:#fff}", _ct);
        await page.Locator("#name").FillAsync("typed", _ct);
        await page.Locator("#name").ClearAsync(_ct);
        await page.Locator("#title").WaitForAsync("visible", null, _ct);
        await page.Locator("#overlay").WaitForAsync("hidden", null, _ct);
        await page.WaitForLoadStateAsync("networkidle", null, _ct);
    }

    [Fact]
    public async Task Reads_are_null_or_empty_when_the_selector_is_absent()
    {
        using var site = BasicSite();
        await using var session = await fixture.LocalBackend.ConnectAsync(Binding(), SessionPolicy.Default, _ct);
        var page = await session.NewPageAsync(_ct);
        await page.GotoAsync(site.Url("/page.html"), null, null, _ct);

        var missing = page.Locator("#does-not-exist");
        (await missing.CountAsync(_ct)).ShouldBe(0);
        (await missing.TextContentAsync(_ct)).ShouldBeNull();
        (await missing.GetAttributeAsync("href", _ct)).ShouldBeNull();
        (await missing.InnerTextAsync(_ct)).ShouldBe(string.Empty);
        (await missing.InnerHTMLAsync(_ct)).ShouldBe(string.Empty);
    }

    [Fact]
    public async Task WaitFor_that_never_resolves_is_a_retryable_timeout()
    {
        using var site = BasicSite();
        await using var session = await fixture.LocalBackend.ConnectAsync(Binding(), SessionPolicy.Default, _ct);
        var page = await session.NewPageAsync(_ct);
        await page.GotoAsync(site.Url("/page.html"), null, null, _ct);

        // #title stays visible, so waiting for it to be "hidden" times out — the real Playwright timeout maps onto the retryable taxonomy.
        await Should.ThrowAsync<BrowserTimeoutException>(
            () => page.Locator("#title").WaitForAsync("hidden", 400, _ct));
    }

    [Fact]
    public async Task Navigates_via_run_and_wait_for_request()
    {
        using var site = BasicSite();
        await using var session = await fixture.LocalBackend.ConnectAsync(Binding(), SessionPolicy.Default, _ct);
        var page = await session.NewPageAsync(_ct);
        await page.GotoAsync(site.Url("/page.html"), null, null, _ct);

        // method-filtered wait (match): the button's POST /api satisfies the POST filter.
        await page.RunAndWaitForRequestAsync(
            () => page.Locator("#btn").ClickAsync(null, _ct),
            site.Url("/api"),
            "POST",
            null,
            _ct);

        // method-filtered wait (mismatch): the same POST /api is rejected by a PUT filter (URL matches, method differs),
        // so the wait times out — covering the negative side of the method comparison.
        await Should.ThrowAsync<BrowserTimeoutException>(() => page.RunAndWaitForRequestAsync(
            () => page.Locator("#btn").ClickAsync(null, _ct),
            site.Url("/api"),
            "PUT",
            600,
            _ct));

        // method-agnostic wait: the anchor navigates (a GET) to /next.html.
        await page.RunAndWaitForRequestAsync(
            () => page.Locator("#go").ClickAsync(null, _ct),
            site.Url("/next.html"),
            null,
            null,
            _ct);

        (await page.Locator("#done").TextContentAsync(_ct)).ShouldBe("Done");
    }

    [Fact]
    public async Task Downloads_bytes_with_the_suggested_filename()
    {
        using var site = new LocalSite()
            .Map("/dl.html", "text/html", "<html><body><a id='d' href='/file.bin' download='report.bin'>dl</a></body></html>")
            .Map("/file.bin", "application/octet-stream", "REPORT-BYTES");
        await using var session = await fixture.LocalBackend.ConnectAsync(Binding(), SessionPolicy.Default, _ct);
        var page = await session.NewPageAsync(_ct);
        await page.GotoAsync(site.Url("/dl.html"), null, null, _ct);

        var download = await page.RunAndWaitForDownloadAsync(
            () => page.Locator("#d").ClickAsync(null, _ct), null, _ct);

        download.SuggestedFilename.ShouldBe("report.bin");
        using var reader = new StreamReader(await download.OpenReadAsync(_ct));
        (await reader.ReadToEndAsync(_ct)).ShouldBe("REPORT-BYTES");
    }

    [Fact]
    public async Task Blocks_a_blocked_resource_type()
    {
        using var site = new LocalSite()
            .Map("/img.html", "text/html", "<html><body><img id='pic' src='/pic.png'><h1 id='ok'>ok</h1></body></html>")
            .Map("/pic.png", "image/png", "PNGDATA");

        var policy = new SessionPolicy([], false, 120000, new RoutePolicy(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "image" },
            new HashSet<string>(StringComparer.Ordinal),
            [],
            0));

        await using var session = await fixture.LocalBackend.ConnectAsync(Binding(), policy, _ct);
        var page = await session.NewPageAsync(_ct);
        await page.GotoAsync(site.Url("/img.html"), "networkidle", null, _ct);

        (await page.Locator("#ok").TextContentAsync(_ct)).ShouldBe("ok"); // document passed through
        site.Hits("/pic.png").ShouldBe(0);                                // the image was aborted before any origin fetch
    }

    [Fact]
    public async Task Serves_a_cacheable_asset_from_the_cross_run_cache()
    {
        // no-store forces the browser to re-request on the second navigation, so both requests reach the route handler.
        using var site = new LocalSite()
            .Map("/c.html", "text/html", "<html><head><link rel='stylesheet' href='/app.css'></head><body>x</body></html>")
            .Map("/app.css", "text/css", "body{color:blue}", cacheControl: "no-store");

        var policy = new SessionPolicy([], false, 120000, new RoutePolicy(
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal) { "stylesheet" },
            [],
            0));

        await using var session = await fixture.LocalBackend.ConnectAsync(Binding(), policy, _ct);

        var first = await session.NewPageAsync(_ct);
        await first.GotoAsync(site.Url("/c.html"), "networkidle", null, _ct);
        var second = await session.NewPageAsync(_ct);
        await second.GotoAsync(site.Url("/c.html"), "networkidle", null, _ct);

        site.Hits("/app.css").ShouldBe(1);   // fetched once from origin; the second was served from the cache
        session.CacheHits.ShouldBe(1);        // and counted as a hit
    }

    [Fact]
    public async Task Region_comes_from_options_or_defaults_to_local()
    {
        await using var withRegion = await fixture.LocalBackend.ConnectAsync(Binding("us-west"), SessionPolicy.Default, _ct);
        withRegion.Region.ShouldBe("us-west");

        await using var withoutRegion = await fixture.LocalBackend.ConnectAsync(Binding(), SessionPolicy.Default, _ct);
        withoutRegion.Region.ShouldBe("local");
    }

    [Fact]
    public async Task Closing_a_page_is_a_clean_teardown()
    {
        // The reopen path closes a page before opening a fresh one; here we exercise the wrapper's CloseAsync directly.
        using var site = BasicSite();
        await using var session = await fixture.LocalBackend.ConnectAsync(Binding(), SessionPolicy.Default, _ct);
        var page = await session.NewPageAsync(_ct);
        await page.GotoAsync(site.Url("/page.html"), null, null, _ct);
        await Should.NotThrowAsync(() => page.CloseAsync(_ct));
    }

    [Fact]
    public async Task Disposing_a_session_closes_the_context_but_not_the_shared_browser()
    {
        using var site = BasicSite();

        var first = await fixture.LocalBackend.ConnectAsync(Binding(), SessionPolicy.Default, _ct);
        var page = await first.NewPageAsync(_ct);
        await page.GotoAsync(site.Url("/page.html"), null, null, _ct);
        await first.DisposeAsync(); // closes only this run's context

        // The shared browser survives — a fresh connect still works.
        await using var second = await fixture.LocalBackend.ConnectAsync(Binding(), SessionPolicy.Default, _ct);
        var page2 = await second.NewPageAsync(_ct);
        await page2.GotoAsync(site.Url("/page.html"), null, null, _ct);
        (await page2.Locator("#title").TextContentAsync(_ct)).ShouldBe("Hello & World");
    }
}
