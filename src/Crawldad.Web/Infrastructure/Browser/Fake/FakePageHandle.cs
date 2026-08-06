using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Crawldad.Web.Infrastructure.Browser.Fake;

/// <summary>
/// A record/replay page (§ Deliverable 2). The "page" is the current state's AngleSharp document; navigation and
/// clicks move between states per the manifest. Documents are parsed once per state and <b>cached</b>, so DOM
/// mutations (<c>fill</c>/<c>clear</c>) persist and are observable after the run — the end-to-end date-fill proof.
/// </summary>
internal sealed class FakePageHandle : IPageHandle
{
    private static readonly HtmlParser _parser = new();

    private readonly FakeBrowserSession _session;
    private readonly FakeManifest _manifest;
    private readonly Dictionary<string, IDocument> _documents = new(StringComparer.Ordinal);
    private readonly List<FakeEmit> _recentRequests = [];

    private FakeState _state;
    private IDownloadHandle? _pendingDownload;

    internal FakePageHandle(FakeBrowserSession session)
    {
        _session = session;
        _manifest = session.Manifest;
        _state = _manifest.InitialState;
    }

    /// <summary>Whether <see cref="CloseAsync"/> was called on this page — asserts the crashed page was torn down on reopen.</summary>
    internal bool CloseAttempted { get; private set; }

    /// <summary>Whether this page has been closed.</summary>
    internal bool Closed { get; private set; }

    public string Url => _state.Url;

    /// <summary>The current state's document, parsed on first entry and cached (mutations persist). Read by locators.</summary>
    internal IDocument CurrentDocument => DocumentFor(_state);

    /// <summary>The current state's name (test inspection).</summary>
    internal string CurrentStateName => _state.Name;

    /// <summary>The (possibly mutated) document for a named state — the date-fill assertion reads the form here.</summary>
    /// <param name="stateName">The state key.</param>
    internal IDocument DocumentForState(string stateName) => DocumentFor(_manifest.State(stateName));

    public Task GotoAsync(string url, string? waitUntil, int? timeoutMs, CancellationToken ct)
    {
        _state = _manifest.ResolveGoto(url);
        return Task.CompletedTask;
    }

    public Task WaitForLoadStateAsync(string state, int? timeoutMs, CancellationToken ct) => Task.CompletedTask;

    public ILocatorHandle Locator(string selector) => FakeLocatorHandle.Css(this, selector);

    public ILocatorHandle GetByTitle(string title) => FakeLocatorHandle.Title(this, title);

    public Task CloseAsync(CancellationToken ct)
    {
        CloseAttempted = true;
        Closed = true;
        return Task.CompletedTask;
    }

    public async Task RunAndWaitForRequestAsync(Func<Task> trigger, string urlPrefix, string? method, int? timeoutMs, CancellationToken ct)
    {
        _recentRequests.Clear();
        await trigger();

        var matched = _recentRequests.Exists(r =>
            r.Url.StartsWith(urlPrefix, StringComparison.Ordinal)
            && (method is null || string.Equals(r.Method, method, StringComparison.Ordinal)));

        if (!matched)
        {
            throw new BrowserTimeoutException($"no request matching '{method ?? "*"} {urlPrefix}' was observed during the trigger");
        }
    }

    public async Task<IDownloadHandle> RunAndWaitForDownloadAsync(Func<Task> trigger, int? timeoutMs, CancellationToken ct)
    {
        // Arm the wait BEFORE the trigger (Playwright semantics): a download-carrying click sets _pendingDownload inside
        // HandleClick, and we hand it back. A trigger that starts no download is a retryable timeout (the 180 s wait).
        _pendingDownload = null;
        await trigger();

        return _pendingDownload
            ?? throw new BrowserTimeoutException("no download was started during the trigger");
    }

    /// <summary>
    /// Applies any manifest transition whose trigger element (in the current state) is the clicked element. Records
    /// the transition's emitted request and swaps state. Clicks that match no transition are no-ops.
    /// </summary>
    /// <param name="element">The element the locator resolved and clicked, or null when the locator matched nothing.</param>
    internal void HandleClick(IElement? element)
    {
        if (element is null)
        {
            return;
        }

        foreach (var transition in _manifest.Transitions)
        {
            if (!string.Equals(transition.From, _state.Name, StringComparison.Ordinal))
            {
                continue;
            }

            if (ReferenceEquals(CurrentDocument.QuerySelector(transition.ClickSelector), element))
            {
                MaybeInject(transition); // a scripted fault (§ Deliverable 3) throws here for its leading attempts

                if (transition.Download is { } download)
                {
                    _pendingDownload = new FakeDownloadHandle(_manifest.ReadFile(download.File), download.SuggestedFilename);
                }

                if (transition.Emit is { } emit)
                {
                    _recentRequests.Add(emit);
                }

                _state = _manifest.State(transition.To);
                return;
            }
        }
    }

    /// <summary>
    /// Fires the transition's scripted fault if it should fail this attempt (§ Deliverable 3). The attempt count is
    /// per-transition on the session, so it advances once per whole-program run and survives a page reopen.
    /// </summary>
    /// <param name="transition">The transition whose click just matched.</param>
    /// <exception cref="BrowserPageCrashedException">On a <c>pageCrashed</c> fault this attempt.</exception>
    /// <exception cref="BrowserTimeoutException">On a <c>timeout</c> fault this attempt.</exception>
    private void MaybeInject(FakeTransition transition)
    {
        if (transition.Inject is not { } inject)
        {
            return;
        }

        var attempt = _session.NextInjectAttempt(transition);
        if (attempt > inject.FailAttempts)
        {
            return; // the scripted failures are used up — this attempt succeeds
        }

        if (string.Equals(inject.Type, "pageCrashed", StringComparison.Ordinal))
        {
            throw new BrowserPageCrashedException($"Page crashed (scripted fault, attempt {attempt})");
        }

        throw new BrowserTimeoutException($"scripted timeout (attempt {attempt})");
    }

    private IDocument DocumentFor(FakeState state)
    {
        if (!_documents.TryGetValue(state.Name, out var document))
        {
            document = _parser.ParseDocument(_manifest.ReadHtml(state));
            _documents[state.Name] = document;
        }

        return document;
    }
}
