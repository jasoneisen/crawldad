using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawldad.Api.Infrastructure.Browser.Fake;

/// <summary>One replayable state: the DOM served and the URL the page reports while in it. <c>Frames</c> keys each
/// iframe's CSS selector to the HTML file served as that frame's document — since it's per-state, an in-frame
/// pagination transition to a new state swaps the grid the frame serves.</summary>
internal sealed record FakeState(string Name, string? GotoUrl, string Url, string HtmlFile, IReadOnlyDictionary<string, string> Frames);

/// <summary>A recorded request a transition emits — checked by <c>RunAndWaitForRequestAsync</c> (urlPrefix + method).</summary>
internal sealed record FakeEmit(string Url, string Method);

/// <summary>A scripted fault attached to a transition: the first <see cref="FailAttempts"/> triggers (across the
/// session lifetime) throw a retryable <c>timeout</c>/<c>pageCrashed</c> condition instead of transitioning; the next
/// trigger succeeds. Setting <see cref="FailAttempts"/> ≥ <c>maxAttempts</c> makes the fault unconditional.</summary>
internal sealed record FakeInject(string Type, int FailAttempts);

/// <summary>A file a transition's click yields as a browser download: bytes come from <see cref="File"/> and
/// <see cref="SuggestedFilename"/> is the HTTP-suggested name — deliberately allowed to differ from the scraped
/// filename cell, exercising the storedAs/internalFilename split.</summary>
internal sealed record FakeDownload(string File, string SuggestedFilename);

/// <summary>A record/replay transition: clicking <see cref="ClickSelector"/> while in <see cref="From"/> (matching
/// <see cref="In"/>'s frame scope, or page-level if null) swaps to <see cref="To"/> — a download link self-loops
/// (<c>to == from</c>) — and optionally emits <see cref="Emit"/> or yields <see cref="Download"/> bytes.</summary>
internal sealed record FakeTransition(string From, string ClickSelector, string? In, string To, FakeEmit? Emit, FakeInject? Inject, FakeDownload? Download);

/// <summary>The loaded, validated manifest plus the content source its states' HTML is read from — a shipped fixture
/// directory (the internal acceptance fixtures) or an in-memory content-addressed page map (a tenant's recorded set).
/// <see cref="Strict"/> is set for a tenant replay so an unrecorded navigation/click fails classified instead of the
/// lenient fallback the internal fixtures keep.</summary>
internal sealed class FakeManifest
{
    private readonly IFixtureContent _content;
    private readonly IReadOnlyDictionary<string, FakeState> _states;

    private FakeManifest(IFixtureContent content, bool strict, string initialState, IReadOnlyDictionary<string, FakeState> states, IReadOnlyList<FakeTransition> transitions)
    {
        _content = content;
        Strict = strict;
        _states = states;
        InitialState = states[initialState];
        Transitions = transitions;
    }

    /// <summary>The state a fresh <c>goto</c> lands on when no <see cref="FakeState.GotoUrl"/> matches.</summary>
    public FakeState InitialState { get; }

    /// <summary>All transitions, in declaration order.</summary>
    public IReadOnlyList<FakeTransition> Transitions { get; }

    /// <summary>Whether this is a strict tenant-replay manifest: an unrecorded goto/click is a terminal
    /// <see cref="FixtureDivergenceException"/>, not the lenient internal fallback (initial state / silent no-op).</summary>
    public bool Strict { get; }

    /// <summary>Resolves a state by name (used when applying a transition's target).</summary>
    public FakeState State(string name) => _states[name];

    /// <summary>The state a navigation to <paramref name="url"/> loads: an exact <see cref="FakeState.GotoUrl"/> match, else the initial state.</summary>
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

    /// <summary>Whether any state records a <c>gotoUrl</c> exactly matching <paramref name="url"/> — the strict-replay
    /// guard for an unrecorded navigation (the lenient path falls back to <see cref="InitialState"/> regardless).</summary>
    public bool HasGotoMatch(string url) =>
        _states.Values.Any(state => string.Equals(state.GotoUrl, url, StringComparison.Ordinal));

    /// <summary>Reads the HTML served for <paramref name="state"/> from the content source.</summary>
    public string ReadHtml(FakeState state) => ReadTextFile(state.HtmlFile);

    /// <summary>Reads a fixture HTML text — the body of a state's DOM or one of its frames' documents.</summary>
    public string ReadTextFile(string key) => _content.ReadText(key);

    /// <summary>Reads a fixture file's raw bytes — the body a <see cref="FakeDownload"/> transition serves.</summary>
    public byte[] ReadFile(string key) => _content.ReadBytes(key);

    /// <summary>Loads and validates <c>manifest.json</c> from a shipped <paramref name="fixtureDir"/> (the internal
    /// acceptance fixtures — never strict).</summary>
    /// <exception cref="FakeBackendException">When the directory or manifest is missing or malformed.</exception>
    public static FakeManifest Load(string fixtureDir)
    {
        var manifestPath = Path.Combine(fixtureDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FakeBackendException($"fixture manifest not found: {manifestPath}");
        }

        return Parse(File.ReadAllText(manifestPath), new DirectoryFixtureContent(fixtureDir), strict: false);
    }

    /// <summary>Builds a manifest from its JSON plus a <paramref name="content"/> source — shared by the directory load
    /// (internal fixtures) and a tenant recorded set (in-memory pages, <paramref name="strict"/>).</summary>
    public static FakeManifest Parse(string manifestJson, IFixtureContent content, bool strict)
    {
        // The manifest is an authored fixture or an engine-produced recording, not free user input: an empty deserialize
        // or an initialState that names no declared state faults with a plain CLR error here rather than adding untested
        // defensive branches.
        var dto = JsonSerializer.Deserialize<ManifestDto>(manifestJson, _serializerOptions)!;

        var states = dto.States.ToDictionary(
            static kvp => kvp.Key,
            kvp => new FakeState(kvp.Key, kvp.Value.GotoUrl, kvp.Value.Url, kvp.Value.Html, kvp.Value.Frames ?? _noFrames),
            StringComparer.Ordinal);

        // A transition carries an emit, a download, or both/neither, and an optional in-frame scope. The null-forgiving
        // derefs below are intentional: a malformed authored fixture faults here rather than adding untested null
        // branches to the hot path.
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

        return new FakeManifest(content, strict, dto.InitialState, states, transitions);
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
