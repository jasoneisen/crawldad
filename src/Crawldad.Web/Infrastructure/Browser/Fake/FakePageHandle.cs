using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Crawldad.Web.Infrastructure.Browser.Fake;

/// <summary>A record/replay page: the "page" is the current state's AngleSharp document; navigation and clicks move
/// between states per the manifest. Documents are parsed once per state and cached, so DOM mutations
/// (<c>fill</c>/<c>clear</c>) persist and are observable after the run.</summary>
internal sealed class FakePageHandle : IPageHandle
{
    private static readonly HtmlParser _parser = new();

    private readonly FakeBrowserSession _session;
    private readonly FakeManifest _manifest;
    private readonly Dictionary<string, IDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<(string State, string Frame), IDocument> _frameDocuments = [];
    private readonly List<FakeEmit> _recentRequests = [];
    private readonly List<string> _injectedStyles = [];

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

    /// <summary>The CSS injected by <c>addStyleTag</c> nodes, in order — observable for test assertion (no layout applied).</summary>
    internal IReadOnlyList<string> InjectedStyles => _injectedStyles;

    /// <summary>Whether this page has been closed.</summary>
    internal bool Closed { get; private set; }

    public string Url => _state.Url;

    /// <summary>The current state's document, parsed on first entry and cached (mutations persist). Read by locators.</summary>
    internal IDocument CurrentDocument => DocumentFor(_state);

    /// <summary>The current state's name (test inspection).</summary>
    internal string CurrentStateName => _state.Name;

    /// <summary>The (possibly mutated) document for a named state — the date-fill assertion reads the form here.</summary>
    internal IDocument DocumentForState(string stateName) => DocumentFor(_manifest.State(stateName));

    public Task GotoAsync(string url, string? waitUntil, int? timeoutMs, CancellationToken ct)
    {
        _state = _manifest.ResolveGoto(url);
        return Task.CompletedTask;
    }

    public Task WaitForLoadStateAsync(string state, int? timeoutMs, CancellationToken ct) => Task.CompletedTask;

    public ILocatorHandle Locator(string selector) => FakeLocatorHandle.Css(this, selector);

    public ILocatorHandle GetByTitle(string title) => FakeLocatorHandle.Title(this, title);

    public ILocatorHandle GetByRole(string role, string? name) => FakeLocatorHandle.Role(this, role, name);

    public ILocatorHandle GetByText(string text) => FakeLocatorHandle.Text(this, text);

    public IFrameHandle FrameLocator(string selector) => new FakeFrameHandle(this, selector);

    public Task AddStyleTagAsync(string content, CancellationToken ct)
    {
        _injectedStyles.Add(content);
        return Task.CompletedTask;
    }

    /// <summary>The (possibly mutated) document of one iframe on the current state, cached per (state, frame) so
    /// in-frame <c>fill</c>/<c>clear</c> persist and a state swap serves new frame content. No content for the
    /// selector yields an empty document — locators find nothing, matching Playwright's "frame absent" behavior.</summary>
    internal IDocument FrameDocument(string frameSelector)
    {
        var key = (_state.Name, frameSelector);
        if (!_frameDocuments.TryGetValue(key, out var document))
        {
            var html = _state.Frames.TryGetValue(frameSelector, out var file) ? _manifest.ReadTextFile(file) : string.Empty;
            document = _parser.ParseDocument(html);
            _frameDocuments[key] = document;
        }

        return document;
    }

    public Task CloseAsync(CancellationToken ct)
    {
        CloseAttempted = true;
        Closed = true;
        return Task.CompletedTask;
    }

    /// <summary>Serves deterministic fake screenshot bytes: the 8-byte PNG signature followed by the current state's
    /// name, so a capture is a valid-looking PNG that varies by page yet never carries real bytes/PII.</summary>
    /// <param name="ct">Unused — the fake captures no real page.</param>
    public Task<byte[]> ScreenshotAsync(CancellationToken ct) =>
        Task.FromResult<byte[]>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Encoding.UTF8.GetBytes(_state.Name)]);

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

    /// <summary>Applies any manifest transition whose trigger element (in the current state) is the clicked element,
    /// then records the emitted request and swaps state. A null <paramref name="element"/> (no match) or a
    /// <paramref name="frame"/> not matching the transition's own scope is a no-op.</summary>
    internal void HandleClick(IElement? element, string? frame)
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

            if (!string.Equals(transition.In, frame, StringComparison.Ordinal))
            {
                continue; // the click's frame scope must match the transition's
            }

            var document = frame is null ? CurrentDocument : FrameDocument(frame);
            if (ReferenceEquals(document.QuerySelector(transition.ClickSelector), element))
            {
                MaybeInject(transition); // a scripted fault throws here for its leading attempts

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

    /// <summary>Fires the transition's scripted fault if it should fail this attempt.</summary>
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
