using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawldad.Web.Infrastructure.Browser.Fake;

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

/// <summary>The loaded, validated <c>manifest.json</c> plus the fixture directory it was loaded from, so the page can
/// read each state's HTML on demand.</summary>
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

    /// <summary>Reads the HTML served for <paramref name="state"/> from the fixture directory.</summary>
    public string ReadHtml(FakeState state) => ReadTextFile(state.HtmlFile);

    /// <summary>Reads a fixture HTML file's text — the body of a state's DOM or one of its frames' documents.</summary>
    public string ReadTextFile(string relativePath) => File.ReadAllText(Path.Combine(_fixtureDir, relativePath));

    /// <summary>Reads a fixture file's raw bytes — the body a <see cref="FakeDownload"/> transition serves.</summary>
    public byte[] ReadFile(string relativePath) => File.ReadAllBytes(Path.Combine(_fixtureDir, relativePath));

    /// <summary>Loads and validates <c>manifest.json</c> from <paramref name="fixtureDir"/>.</summary>
    /// <exception cref="FakeBackendException">When the directory or manifest is missing or malformed.</exception>
    public static FakeManifest Load(string fixtureDir)
    {
        var manifestPath = Path.Combine(fixtureDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FakeBackendException($"fixture manifest not found: {manifestPath}");
        }

        // The manifest is an authored fixture, not user input: an empty deserialize or an initialState that names no
        // declared state faults with a plain CLR error here rather than adding untested defensive branches.
        var dto = JsonSerializer.Deserialize<ManifestDto>(File.ReadAllText(manifestPath), _serializerOptions)!;

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
