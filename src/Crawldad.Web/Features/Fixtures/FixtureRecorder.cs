using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crawldad.Web.Features.Runs.Interpreter;
using Crawldad.Web.Infrastructure.Browser;

namespace Crawldad.Web.Features.Fixtures;

/// <summary>The record-mode engine: driven by the interpreter as a live run executes, it banks each settled page DOM as
/// a content-addressed state and each click as a transition, producing a manifest the <c>fixture</c> replay backend
/// (via <see cref="Infrastructure.Browser.Fake.FakeManifest"/>) replays deterministically. The recorded subset is
/// deliberately linear — state-per-navigation/click, page-level CSS clicks, postback emits — so a captured session can
/// faithfully replay itself; anything outside it (a download, an in-frame or non-CSS click) fails the record run
/// classified rather than banking a set that cannot replay.</summary>
internal sealed class FixtureRecorder : IFixtureRecorder
{
    /// <summary>The terminal failure code for a session the recorder cannot capture (an unrecordable operation, or a
    /// recording that outgrew its page/byte cap). A record run raising it persists no set.</summary>
    public const string UnrecordableCode = "fixture_unrecordable";

    private readonly Func<string, string> _scrubUrl;
    private readonly int _maxStates;
    private readonly long _maxBytes;

    // Content-addressed pages (sha256 hex -> HTML) and the states/transitions built over them, in first-seen order.
    private readonly Dictionary<string, string> _pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StateBuilder> _statesBySha = new(StringComparer.Ordinal);
    private readonly List<StateBuilder> _states = [];
    private readonly List<TransitionBuilder> _transitions = [];

    private string? _initialState;
    private string? _currentState;
    private TransitionBuilder? _open; // a click transition awaiting its settled to-state
    private EmitDto? _pendingEmit;
    private long _totalBytes;

    /// <summary>Creates a recorder that scrubs every persisted URL through <paramref name="scrubUrl"/> — the same
    /// credential redaction (exact registered secrets + apiKey/token/signingKey params) the run timeline applies to a
    /// <c>Navigated</c> URL, so no manifest URL echoes a secret over the API — bounded by <paramref name="maxStates"/>
    /// distinct pages and <paramref name="maxBytes"/> total HTML (a runaway session fails classified). Secrets are only in
    /// scope during the record run, so scrubbing must happen here, not at GET time.</summary>
    public FixtureRecorder(Func<string, string> scrubUrl, int maxStates = DefaultMaxStates, long maxBytes = DefaultMaxBytes)
    {
        _scrubUrl = scrubUrl;
        _maxStates = maxStates;
        _maxBytes = maxBytes;
    }

    /// <summary>Discards everything banked so far — called at the start of each interpreter retry attempt so a program
    /// re-run (after a transient <c>timeout</c>/<c>pageCrashed</c>) records only the final successful pass, never a
    /// duplicated/mis-wired merge of a failed attempt's states and transitions.</summary>
    public void Reset()
    {
        _pages.Clear();
        _statesBySha.Clear();
        _states.Clear();
        _transitions.Clear();
        _initialState = null;
        _currentState = null;
        _open = null;
        _pendingEmit = null;
        _totalBytes = 0;
    }

    /// <summary>The default cap on distinct recorded pages — well above any representative record-once session.</summary>
    public const int DefaultMaxStates = 500;

    /// <summary>The default cap on total recorded HTML bytes (32 MiB) — a bounded set fits comfortably in a Marten doc.</summary>
    public const long DefaultMaxBytes = 32L << 20;

    public async ValueTask OnNavigatedAsync(string url, IPageHandle page, CancellationToken ct)
    {
        var state = await SettleAsync(page, ct);
        state.GotoUrl ??= _scrubUrl(url); // the first goto that lands on this page fixes its gotoUrl (scrubbed; replay resolves navigations by it)
        _initialState ??= state.Name; // the run's first navigation is the manifest's initial state
    }

    public async ValueTask OnClickAsync(string? cssSelector, bool inFrame, IPageHandle page, CancellationToken ct)
    {
        if (inFrame)
        {
            throw Unrecordable("an in-frame click");
        }

        if (cssSelector is null)
        {
            throw Unrecordable("a non-CSS (structured) click selector");
        }

        await SettleAsync(page, ct); // the pre-click settled DOM is this transition's from-state
        _open = new TransitionBuilder(_currentState!, cssSelector, _pendingEmit);
        _transitions.Add(_open); // its To is filled by the next settle (kept by reference in the list)
    }

    public void SetPendingEmit(string urlPrefix, string? method) =>
        _pendingEmit = new EmitDto(_scrubUrl(urlPrefix), method ?? "GET"); // scrubbed; echoes the payload's own urlPrefix so replay's StartsWith match holds

    public void ClearPendingEmit() => _pendingEmit = null;

    public void RejectUnrecordable(string operation) => throw Unrecordable(operation);

    // The typed terminal failure a record run raises for an unrecordable session — returned (not thrown) so the internal
    // call sites raise it with a `throw` expression the coverage gate accounts for exactly.
    private static CrawldadFailureException Unrecordable(string operation) =>
        new("terminal", UnrecordableCode, $"record mode cannot capture {operation} — the session was not recorded");

    public async ValueTask FinalizeAsync(IPageHandle page, CancellationToken ct) => await SettleAsync(page, ct);

    /// <summary>Assembles the recorded set: the manifest JSON (initial state + state graph + transitions) plus the
    /// content-addressed page map. Every recorded state is a navigable page or a transition endpoint (the settle-at-each-
    /// boundary model never produces an orphan), so all are kept. A session that recorded no initial navigation is
    /// unrecordable — a record run must begin with a goto.</summary>
    public RecordedFixture Build()
    {
        if (_initialState is null)
        {
            throw Unrecordable("a session with no initial navigation (a record run must begin with a goto)");
        }

        var pages = _states.ToDictionary(static s => s.Sha, s => _pages[s.Sha], StringComparer.Ordinal);
        var manifest = new ManifestDto(
            _initialState,
            _states.ToDictionary(static s => s.Name, static s => new StateDto(s.GotoUrl, s.Url, s.Sha), StringComparer.Ordinal),
            [.. _transitions.Select(static t => new TransitionDto(t.From, new OnDto(t.Click), t.To!, t.Emit))]);

        var manifestJson = JsonSerializer.Serialize(manifest, _serializerOptions);
        return new RecordedFixture(manifestJson, pages, pages.Count, _transitions.Count, _totalBytes);
    }

    // Snapshots the page's settled DOM as a content-addressed state (deduping identical pages by hash), closes any open
    // click transition onto it, and makes it the current state.
    private async ValueTask<StateBuilder> SettleAsync(IPageHandle page, CancellationToken ct)
    {
        var html = await page.ContentAsync(ct);
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(html)));

        if (!_statesBySha.TryGetValue(sha, out var state))
        {
            _totalBytes += Encoding.UTF8.GetByteCount(html);
            if (_states.Count >= _maxStates || _totalBytes > _maxBytes)
            {
                throw Unrecordable($"a session exceeding the recording cap ({_maxStates} pages / {_maxBytes} bytes)");
            }

            state = new StateBuilder($"s{_states.Count}", sha, _scrubUrl(page.Url)); // the reported URL is scrubbed too (a postback URL can bear a credential param)
            _pages[sha] = html;
            _statesBySha[sha] = state;
            _states.Add(state);
        }

        if (_open is not null)
        {
            _open.To = state.Name;
            _open = null;
        }

        _currentState = state.Name;
        return state;
    }

    private static readonly JsonSerializerOptions _serializerOptions =
        new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // A page state under construction: its assigned name, content hash (the html key), reported URL, and the gotoUrl a
    // navigation fixed on it (null for a state only ever reached by a click).
    private sealed class StateBuilder(string name, string sha, string url)
    {
        public string Name { get; } = name;

        public string Sha { get; } = sha;

        public string Url { get; } = url;

        public string? GotoUrl { get; set; }
    }

    // A click transition under construction: its from-state and CSS selector (known when the click fires) plus its
    // to-state (filled by the next settle) and any postback emit armed by an enclosing waitForRequest.
    private sealed class TransitionBuilder(string from, string click, EmitDto? emit)
    {
        public string From { get; } = from;

        public string Click { get; } = click;

        public EmitDto? Emit { get; } = emit;

        public string? To { get; set; }
    }

    // The serialisation shape — property names match FakeManifest's reader so a recorded manifest round-trips through the
    // same replay engine the internal fixtures use.
    private sealed record ManifestDto(
        [property: JsonPropertyName("initialState")] string InitialState,
        [property: JsonPropertyName("states")] IReadOnlyDictionary<string, StateDto> States,
        [property: JsonPropertyName("transitions")] IReadOnlyList<TransitionDto> Transitions)
    {
        [JsonPropertyName("manifest")]
        public string Manifest => "1";
    }

    private sealed record StateDto(
        [property: JsonPropertyName("gotoUrl")] string? GotoUrl,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("html")] string Html);

    private sealed record TransitionDto(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("on")] OnDto On,
        [property: JsonPropertyName("to")] string To,
        [property: JsonPropertyName("emit")] EmitDto? Emit);

    private sealed record OnDto([property: JsonPropertyName("click")] string Click);

    private sealed record EmitDto(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("method")] string Method);
}

/// <summary>The output of a completed recording: the manifest JSON, the content-addressed page map (sha256 → HTML), and
/// the summary counts a <see cref="FixtureSet"/> stores.</summary>
internal sealed record RecordedFixture(
    string ManifestJson,
    IReadOnlyDictionary<string, string> Pages,
    int PageCount,
    int TransitionCount,
    long TotalBytes);
