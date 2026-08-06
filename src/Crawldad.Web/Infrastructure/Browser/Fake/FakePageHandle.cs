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

    private readonly FakeManifest _manifest;
    private readonly Dictionary<string, IDocument> _documents = new(StringComparer.Ordinal);
    private readonly List<FakeEmit> _recentRequests = [];

    private FakeState _state;

    internal FakePageHandle(FakeManifest manifest)
    {
        _manifest = manifest;
        _state = manifest.InitialState;
    }

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
                _recentRequests.Add(transition.Emit);
                _state = _manifest.State(transition.To);
                return;
            }
        }
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
