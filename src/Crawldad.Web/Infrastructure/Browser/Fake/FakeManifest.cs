using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawldad.Web.Infrastructure.Browser.Fake;

/// <summary>One replayable state: the DOM served and the URL the page reports while in it (§ Deliverable 1).</summary>
/// <param name="Name">The state key.</param>
/// <param name="GotoUrl">The URL that, when navigated, loads this state; null for states reached only by transition.</param>
/// <param name="Url">The URL <c>page.Url</c> reports while in this state.</param>
/// <param name="HtmlFile">The HTML file (relative to the fixture dir) served for this state.</param>
/// <param name="Frames">
/// The content of each iframe on this state, keyed by the iframe element's CSS selector → the HTML file served as that
/// frame's document (§ frames, Phase 3). Empty when the state carries no iframes. Because frame content is per-state,
/// an in-frame pagination transition to a new state swaps the grid the frame serves — reproducing the reference's
/// attachments postback re-render (LJCMGClient.cs:531-621).
/// </param>
internal sealed record FakeState(string Name, string? GotoUrl, string Url, string HtmlFile, IReadOnlyDictionary<string, string> Frames);

/// <summary>A recorded request a transition emits — checked by <c>RunAndWaitForRequestAsync</c> (urlPrefix + method).</summary>
/// <param name="Url">The absolute request URL.</param>
/// <param name="Method">The HTTP method (e.g. <c>POST</c>).</param>
internal sealed record FakeEmit(string Url, string Method);

/// <summary>
/// A scripted fault (§ Deliverable 3) attached to a transition: the first <see cref="FailAttempts"/> times the
/// transition is triggered <em>across the session lifetime</em>, the click throws a retryable browser condition
/// (<c>timeout</c> ⇒ <see cref="BrowserTimeoutException"/>, <c>pageCrashed</c> ⇒ <see cref="BrowserPageCrashedException"/>)
/// instead of transitioning; the next trigger succeeds. An unconditional fault (the exhaustion scenario) is expressed
/// by setting <see cref="FailAttempts"/> at or above the run's <c>maxAttempts</c>. The attempt count is per-transition,
/// held on the session, so it persists across a page reopen (making pageCrashed-then-succeed work) but resets with a
/// fresh session (a new run).
/// </summary>
/// <param name="Type">The fault kind: <c>timeout</c> or <c>pageCrashed</c>.</param>
/// <param name="FailAttempts">How many leading triggers fail before the transition succeeds.</param>
internal sealed record FakeInject(string Type, int FailAttempts);

/// <summary>A file a transition's click yields as a browser download (§ Deliverable 2): the bytes come from
/// <see cref="File"/> (relative to the fixture dir) and <see cref="SuggestedFilename"/> is the download's HTTP-suggested
/// name — deliberately allowed to differ from the scraped filename cell, exercising the §9.3 storedAs/internalFilename split.</summary>
/// <param name="File">The fixture file whose bytes are served as the download body.</param>
/// <param name="SuggestedFilename">The download's suggested filename (source of the engine's stored-blob extension).</param>
internal sealed record FakeDownload(string File, string SuggestedFilename);

/// <summary>A record/replay transition: clicking the element matching <see cref="ClickSelector"/> while in
/// <see cref="From"/> swaps to <see cref="To"/>, optionally emits <see cref="Emit"/>, and optionally yields
/// <see cref="Download"/> bytes.</summary>
/// <param name="From">The state the transition applies in.</param>
/// <param name="ClickSelector">CSS of the element whose click fires the transition, resolved against the page document
/// (when <see cref="In"/> is null) or the named frame's document.</param>
/// <param name="In">The iframe selector the click happens inside (§ frames), or null for a page-level click. The click's
/// frame must match this for the transition to fire, so an in-frame pagination/download click never triggers a
/// page-level transition and vice versa.</param>
/// <param name="To">The state to switch to (a download link is a self-loop: <c>to == from</c>).</param>
/// <param name="Emit">The request recorded during the click, or null. Phase 2 relaxes P1's postback-mandatory rule so a
/// pure download click (which fires no navigation postback) needs no emit.</param>
/// <param name="Inject">An optional scripted fault fired instead of the transition for its leading attempts (§ Deliverable 3).</param>
/// <param name="Download">An optional download the click starts (§ Deliverable 2), captured by <c>RunAndWaitForDownloadAsync</c>.</param>
internal sealed record FakeTransition(string From, string ClickSelector, string? In, string To, FakeEmit? Emit, FakeInject? Inject, FakeDownload? Download);

/// <summary>
/// The loaded, validated <c>manifest.json</c> (§ Deliverable 1) plus the fixture directory it was loaded from, so the
/// page can read each state's HTML on demand. Phase 2's injectable timeout/failure blocks slot onto the DTOs below
/// without touching the interpreter or the seam.
/// </summary>
internal sealed class FakeManifest
{
    private readonly string _fixtureDir;
    private readonly IReadOnlyDictionary<string, FakeState> _states;

    private FakeManifest(string fixtureDir, string initialState, IReadOnlyDictionary<string, FakeState> states, IReadOnlyList<FakeTransition> transitions)
    {
        _fixtureDir = fixtureDir;
        _states = states;
        InitialState = states[initialState];
        Transitions = transitions;
    }

    /// <summary>The state a fresh <c>goto</c> lands on when no <see cref="FakeState.GotoUrl"/> matches.</summary>
    public FakeState InitialState { get; }

    /// <summary>All transitions, in declaration order.</summary>
    public IReadOnlyList<FakeTransition> Transitions { get; }

    /// <summary>Resolves a state by name (used when applying a transition's target).</summary>
    /// <param name="name">The state key.</param>
    public FakeState State(string name) => _states[name];

    /// <summary>The state a navigation to <paramref name="url"/> loads: an exact <see cref="FakeState.GotoUrl"/> match, else the initial state.</summary>
    /// <param name="url">The navigation target URL.</param>
    public FakeState ResolveGoto(string url)
    {
        foreach (var state in _states.Values)
        {
            if (string.Equals(state.GotoUrl, url, StringComparison.Ordinal))
            {
                return state;
            }
        }

        return InitialState;
    }

    /// <summary>Reads the HTML served for <paramref name="state"/> from the fixture directory.</summary>
    /// <param name="state">The state whose DOM to load.</param>
    public string ReadHtml(FakeState state) => ReadTextFile(state.HtmlFile);

    /// <summary>Reads a fixture HTML file's text — the body of a state's DOM or one of its frames' documents.</summary>
    /// <param name="relativePath">The file path relative to the fixture directory.</param>
    public string ReadTextFile(string relativePath) => File.ReadAllText(Path.Combine(_fixtureDir, relativePath));

    /// <summary>Reads a fixture file's raw bytes — the body a <see cref="FakeDownload"/> transition serves (§ Deliverable 2).</summary>
    /// <param name="relativePath">The file path relative to the fixture directory.</param>
    public byte[] ReadFile(string relativePath) => File.ReadAllBytes(Path.Combine(_fixtureDir, relativePath));

    /// <summary>Loads and validates <c>manifest.json</c> from <paramref name="fixtureDir"/>.</summary>
    /// <param name="fixtureDir">The absolute fixture directory (contains <c>manifest.json</c> and the state HTML files).</param>
    /// <returns>The loaded manifest.</returns>
    /// <exception cref="FakeBackendException">When the directory or manifest is missing or malformed.</exception>
    public static FakeManifest Load(string fixtureDir)
    {
        var manifestPath = Path.Combine(fixtureDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FakeBackendException($"fixture manifest not found: {manifestPath}");
        }

        // The manifest is an authored fixture, not user input: an empty deserialize or an initialState that names no
        // declared state faults with a plain CLR error here rather than adding untested defensive branches (Phase 3
        // adds save-time payload validation; fixture validation would live beside it).
        var dto = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(manifestPath), _serializerOptions)!;

        var states = dto.States.ToDictionary(
            static kvp => kvp.Key,
            kvp => new FakeState(kvp.Key, kvp.Value.GotoUrl, kvp.Value.Url, kvp.Value.Html, kvp.Value.Frames ?? _noFrames),
            StringComparer.Ordinal);

        // A transition carries an emit (a navigation postback), a download (§ Deliverable 2), or both/neither — a pure
        // download click has no emit — and an optional in-frame scope (§ frames). The null-forgiving derefs are
        // intentional: a malformed authored fixture faults here rather than adding untested null branches to the hot path.
        var transitions = dto.Transitions
            .Select(static t => new FakeTransition(
                t.From,
                t.On.Click,
                t.On.In,
                t.To,
                t.Emit is null ? null : new FakeEmit(t.Emit.Url, t.Emit.Method),
                t.Inject is null ? null : new FakeInject(t.Inject.Type, t.Inject.FailAttempts!.Value),
                t.Download is null ? null : new FakeDownload(t.Download.File, t.Download.SuggestedFilename)))
            .ToList();

        return new FakeManifest(fixtureDir, dto.InitialState, states, transitions);
    }

    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, string> _noFrames =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private sealed record ManifestDto(
        [property: JsonPropertyName("initialState")] string InitialState,
        [property: JsonPropertyName("states")] Dictionary<string, StateDto> States,
        [property: JsonPropertyName("transitions")] List<TransitionDto> Transitions);

    private sealed record StateDto(
        [property: JsonPropertyName("gotoUrl")] string? GotoUrl,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("html")] string Html,
        [property: JsonPropertyName("frames")] Dictionary<string, string>? Frames);

    private sealed record TransitionDto(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("on")] OnDto On,
        [property: JsonPropertyName("to")] string To,
        [property: JsonPropertyName("emit")] EmitDto? Emit,
        [property: JsonPropertyName("inject")] InjectDto? Inject,
        [property: JsonPropertyName("download")] DownloadDto? Download);

    private sealed record OnDto(
        [property: JsonPropertyName("click")] string Click,
        [property: JsonPropertyName("in")] string? In);

    private sealed record EmitDto(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("method")] string Method);

    private sealed record InjectDto(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("failAttempts")] int? FailAttempts);

    private sealed record DownloadDto(
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("suggestedFilename")] string SuggestedFilename);
}
