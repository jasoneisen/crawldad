using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using Crawldad.Web.Infrastructure.Browser.Fake;

namespace Crawldad.Tests.Support;

/// <summary>An in-process origin serving a fixture's corpus to real headless Chromium (zero third-party traffic),
/// driven by the same <see cref="FakeManifest"/> the record/replay fake uses. Downloads need a real loopback
/// <see cref="HttpListener"/> (fulfilled downloads yield no readable bytes); clicks need the injected <see cref="_transitionScript"/> since captured anchors don't navigate on their own.</summary>
internal sealed class FixtureSite : IDisposable
{
    /// <summary>The canonical origin the corpus is served under, so <c>page.Url</c> and <c>waitForRequest</c> see the real Accela URLs the goldens are built from.</summary>
    public const string Origin = "https://aca-prod.accela.com";

    private readonly FakeManifest _manifest;
    private readonly Lock _gate = new();
    private readonly HttpListener _downloads = new();
    private FakeState? _current;
    private int _frameNavs;

    /// <summary>Loads the fixture's manifest (the same loader and manifest the fake uses) and starts the loopback download listener.</summary>
    /// <param name="fixtureDir">The absolute fixture directory.</param>
    public FixtureSite(string fixtureDir)
    {
        _manifest = FakeManifest.Load(fixtureDir);
        DownloadBase = $"http://127.0.0.1:{Net.FreePort()}";
        _downloads.Prefixes.Add(DownloadBase + "/");
        _downloads.Start();
        _ = Task.Run(ServeDownloadsAsync);
    }

    /// <summary>The loopback origin the injected download links point at; allowed through by the route handler so downloads are genuine network responses.</summary>
    public string DownloadBase { get; }

    /// <summary>Answers one intercepted canonical-origin request from the corpus. Serialized so a concurrent document +
    /// iframe request pair never race the current-state cursor.</summary>
    public FixtureResponse Respond(string method, string url, string? postBody)
    {
        lock (_gate)
        {
            var uri = new Uri(url);
            return uri.AbsolutePath switch
            {
                "/__cf_frame__" => Frame(QueryValue(uri, "sel")),
                "/__cf_frame_nav__" => FrameNav(Index(uri)),
                _ when string.Equals(method, "POST", StringComparison.Ordinal) => Postback(postBody),
                _ => Document(url),
            };
        }
    }

    // A document GET is a goto: resolve the state and, when its canonical url differs from the requested one (the
    // record-09 Error.aspx redirect), rewrite the address at parse time — a fulfilled 302 is followed by Chromium
    // OUTSIDE route interception, so the follow-up request would escape to the real network. The payloads goto once.
    private FixtureResponse Document(string url)
    {
        _current ??= _manifest.ResolveGoto(url);
        var html = TransformPage(_manifest.ReadHtml(_current));
        if (!string.Equals(_current.Url, url, StringComparison.Ordinal))
        {
            html = InjectReplaceState(html, _current.Url);
        }

        return Html(html);
    }

    // A postback POST carries the matched transition's index; apply it (emit transition → new page document).
    private FixtureResponse Postback(string? postBody)
    {
        Apply(TransitionIndexFromBody(postBody));
        return Html(TransformPage(_manifest.ReadHtml(_current!)));
    }

    // The iframe document for the current state (empty when the state serves no content for that iframe — Playwright's
    // "frame absent" resolves to a zero match set, matching the fake).
    private FixtureResponse Frame(string sel)
    {
        var key = "#" + sel;
        var html = _current is not null && _current.Frames.TryGetValue(key, out var file)
            ? _manifest.ReadTextFile(file)
            : "<!DOCTYPE html><html><body></body></html>";
        return Html(TransformFrame(html, key));
    }

    // An in-frame navigation transition (attachment pagination) carries no emit: apply it, then serve the NEW state's
    // content for the iframe the click happened in, so the frame re-renders the next page's grid.
    private FixtureResponse FrameNav(int index)
    {
        var frameSelector = _manifest.Transitions[index].In!;
        _frameNavs++;
        Apply(index);
        return Frame(frameSelector[1..]); // strip the leading '#'; Frame re-adds it
    }

    private void Apply(int index) => _current = _manifest.State(_manifest.Transitions[index].To);

    // ----- the loopback download listener ------------------------------------

    private async Task ServeDownloadsAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _downloads.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break; // listener stopped
            }
            catch (ObjectDisposedException)
            {
                break; // listener closed
            }

            ServeDownload(context);
        }
    }

    // A download transition streams the fixture bytes with the manifest's suggested filename via Content-Disposition —
    // a real, genuine browser download whose bytes RunAndWaitForDownloadAsync reads. The transition index is on
    // the query; no session state is touched (a download is a self-loop, to == from).
    private void ServeDownload(HttpListenerContext context)
    {
        try
        {
            var index = int.Parse(QueryValue(context.Request.Url!, "index"), CultureInfo.InvariantCulture);
            var download = _manifest.Transitions[index].Download!;
            var body = _manifest.ReadFile(download.File);
            context.Response.ContentType = "application/octet-stream";
            context.Response.AddHeader("Content-Disposition", $"attachment; filename=\"{download.SuggestedFilename}\"");
            context.Response.OutputStream.Write(body);
            context.Response.Close();
        }
        catch (HttpListenerException)
        {
            // client went away — irrelevant to the test
        }
        catch (IOException)
        {
            // ditto
        }
    }

    // ----- HTML transforms (serve-time; the fixture files are untouched) ------

    private string TransformPage(string html)
    {
        html = RewriteIframes(html);
        html = InjectScaffold(html);
        return InjectScript(html, Scope(frameSelector: null));
    }

    private string TransformFrame(string html, string frameSelector) =>
        InjectScript(SelectedPage(html), Scope(frameSelector));

    // Renders the pagination's selected-page number to the current position (page 1 initially, +1 per in-frame nav) so
    // the payload's page-number wait resolves in real Chromium. Needed because the cyclic 50-page-cap fixture's frame
    // statically reads "1" — without this, a real wait for the next page number would hang forever.
    private string SelectedPage(string html) =>
        _selectedPage.Replace(html, m => $"{m.Groups["open"].Value}{_frameNavs + 1}</span>");

    // Repoint every iframe at the frame endpoint (keyed by the iframe's own id) and drop its other attributes — the fake
    // serves frame content from the manifest, not from src="about:blank", and a lingering title="attachments" would make
    // getByTitle("Attachments") ambiguous with the tab anchor.
    private static string RewriteIframes(string html) =>
        _iframe.Replace(html, m => $"<iframe id=\"{m.Groups["id"].Value}\" src=\"{Origin}/__cf_frame__?sel={m.Groups["id"].Value}\">");

    // A real Accela CapDetail page carries the record-detail tab controls the payload clicks unconditionally; the
    // synthetic minimal fixtures omit them. Inject a no-op scaffold into record pages (marked by the record-number
    // label) that lack it, so the clicks resolve to real, actionable elements instead of auto-waiting to a timeout.
    private static string InjectScaffold(string html)
    {
        if (!html.Contains("ctl00_PlaceHolderMain_lblPermitNumber", StringComparison.Ordinal)
            || html.Contains("id=\"imgMoreDetail\"", StringComparison.Ordinal))
        {
            return html;
        }

        return _bodyOpen.Replace(html, m => m.Value + _scaffold, 1);
    }

    // The redirect analogue for a route-fulfilled world: a same-origin history.replaceState injected at the top of
    // <body> runs during parse — before "load", so before the payload's guard reads pageUrl() — making page.Url report
    // the state's own url without a second, un-interceptable request.
    private static string InjectReplaceState(string html, string url)
    {
        var script = $"<script>history.replaceState(null,\"\",{JsonSerializer.Serialize(url)});</script>";
        return _bodyOpen.IsMatch(html)
            ? _bodyOpen.Replace(html, m => m.Value + script, 1)
            : script + html;
    }

    private string InjectScript(string html, JsonArray transitions)
    {
        var body = _transitionScript
            .Replace("__T__", transitions.ToJsonString(), StringComparison.Ordinal)
            .Replace("__D__", JsonSerializer.Serialize(DownloadBase), StringComparison.Ordinal);
        var script = $"<script>{body}</script>";
        return _bodyClose.IsMatch(html)
            ? _bodyClose.Replace(html, script + "</body>", 1)
            : html + script;
    }

    // The transitions the injected script drives for one scope: page-level (frameSelector null) or one iframe's.
    private JsonArray Scope(string? frameSelector)
    {
        var array = new JsonArray();
        for (var i = 0; i < _manifest.Transitions.Count; i++)
        {
            var t = _manifest.Transitions[i];
            if (!string.Equals(t.From, _current!.Name, StringComparison.Ordinal)
                || !string.Equals(t.In, frameSelector, StringComparison.Ordinal))
            {
                continue;
            }

            array.Add(new JsonObject
            {
                ["sel"] = t.ClickSelector,
                ["index"] = i,
                ["action"] = t.Download is not null ? "download" : t.Emit is not null ? "postback" : "framenav",
                ["emitUrl"] = t.Emit?.Url,
                ["emitMethod"] = t.Emit?.Method,
            });
        }

        return array;
    }

    // ----- request parsing ---------------------------------------------------

    private static int Index(Uri uri) => int.Parse(QueryValue(uri, "index"), CultureInfo.InvariantCulture);

    private static int TransitionIndexFromBody(string? postBody) =>
        int.Parse(FormValue(postBody, "__cf_transition__"), CultureInfo.InvariantCulture);

    private static string QueryValue(Uri uri, string key) => Field(uri.Query.TrimStart('?'), key);

    private static string FormValue(string? body, string key) => Field(body ?? string.Empty, key);

    private static string Field(string encoded, string key)
    {
        foreach (var pair in encoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && string.Equals(pair[..eq], key, StringComparison.Ordinal))
            {
                return WebUtility.UrlDecode(pair[(eq + 1)..]);
            }
        }

        throw new InvalidOperationException($"fixture request missing field '{key}'");
    }

    private static FixtureResponse Html(string html) =>
        new(200, "text/html; charset=utf-8", null, Encoding.UTF8.GetBytes(html));

    public void Dispose()
    {
        _downloads.Stop();
        _downloads.Close();
    }

    private static readonly Regex _iframe = new("<iframe\\b[^>]*?\\bid=\"(?<id>[^\"]+)\"[^>]*?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
    private static readonly Regex _bodyOpen = new("<body[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex _bodyClose = new("</body>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex _selectedPage = new("(?<open><span class=\"SelectedPageButton\">)[^<]*</span>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    private const string _scaffold =
        "<a id=\"imgMoreDetail\" href=\"#\">More Details</a>" +
        "<a id=\"imgParcel\" href=\"#\">Parcel</a>" +
        "<a id=\"recordInfoMenu\" title=\"Record Info menu, press tab to expand\" href=\"#\">Record Info</a>" +
        "<a id=\"attachmentsTab\" title=\"Attachments\" href=\"#\">Attachments</a>";

    // Turns a captured click matching a transition selector into the real browser action it models. Capturing phase +
    // preventDefault so the synthetic anchors' native behaviour never interferes. A download NAVIGATES to the loopback
    // listener (D, real bytes) — a plain navigation whose Content-Disposition: attachment response the browser turns into
    // a genuine download. It must NOT be a cross-origin `<a download>` click: the download attribute is same-origin-only,
    // so a cross-origin download click is silently dropped by headless Chromium (no request, no download event) — which is
    // exactly how the record-01/03/06 attachment scrapes hung forever on the 2-core CI runner (issue #95) while passing
    // locally. Postbacks/frame-navs also go to the canonical origin (O) so page.Url/waitForRequest see real URLs.
    private const string _transitionScript =
        "(function(){var O=\"https://aca-prod.accela.com\";var D=__D__;var T=__T__;" +
        "document.addEventListener(\"click\",function(e){var el=e.target;if(!el||!el.closest){return;}" +
        "for(var i=0;i<T.length;i++){var t=T[i];if(el.closest(t.sel)){e.preventDefault();" +
        "if(t.action===\"postback\"){var f=document.createElement(\"form\");f.method=t.emitMethod;f.action=t.emitUrl;" +
        "var n=document.createElement(\"input\");n.type=\"hidden\";n.name=\"__cf_transition__\";n.value=\"\"+t.index;" +
        "f.appendChild(n);document.body.appendChild(f);f.submit();}" +
        "else if(t.action===\"download\"){window.location.assign(D+\"/d?index=\"+t.index);}" +
        "else{window.location.assign(O+\"/__cf_frame_nav__?index=\"+t.index);}return;}}},true);})();";
}

/// <summary>One fixture-site response to fulfill. Always a body — never a 302: Chromium follows a fulfilled redirect
/// outside route interception, so a redirecting state is served via a parse-time <c>history.replaceState</c> instead.</summary>
internal sealed record FixtureResponse(
    int Status,
    string ContentType,
    IReadOnlyDictionary<string, string>? Headers,
    byte[] Body);
