using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crawldad.Web.Infrastructure.Browser.Fake;

/// <summary>One replayable state: the DOM served and the URL the page reports while in it (§ Deliverable 1).</summary>
/// <param name="Name">The state key.</param>
/// <param name="GotoUrl">The URL that, when navigated, loads this state; null for states reached only by transition.</param>
/// <param name="Url">The URL <c>page.Url</c> reports while in this state.</param>
/// <param name="HtmlFile">The HTML file (relative to the fixture dir) served for this state.</param>
internal sealed record FakeState(string Name, string? GotoUrl, string Url, string HtmlFile);

/// <summary>A recorded request a transition emits — checked by <c>RunAndWaitForRequestAsync</c> (urlPrefix + method).</summary>
/// <param name="Url">The absolute request URL.</param>
/// <param name="Method">The HTTP method (e.g. <c>POST</c>).</param>
internal sealed record FakeEmit(string Url, string Method);

/// <summary>A record/replay transition: clicking the element matching <see cref="ClickSelector"/> while in
/// <see cref="From"/> swaps to <see cref="To"/> and (optionally) emits <see cref="Emit"/>.</summary>
/// <param name="From">The state the transition applies in.</param>
/// <param name="ClickSelector">CSS of the element whose click fires the transition.</param>
/// <param name="To">The state to switch to.</param>
/// <param name="Emit">The request recorded during the click. Mandatory in P1 (every transition is a postback);
/// Phase 2 relaxes this to model pure client-side state swaps.</param>
internal sealed record FakeTransition(string From, string ClickSelector, string To, FakeEmit Emit);

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
    public string ReadHtml(FakeState state) => File.ReadAllText(Path.Combine(_fixtureDir, state.HtmlFile));

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
            kvp => new FakeState(kvp.Key, kvp.Value.GotoUrl, kvp.Value.Url, kvp.Value.Html),
            StringComparer.Ordinal);

        // P1 contract: every transition declares an emit (a postback). The null-forgiving deref is intentional —
        // a malformed authored fixture faults here rather than adding an untested null branch to the hot path.
        var transitions = dto.Transitions
            .Select(static t => new FakeTransition(t.From, t.On.Click, t.To, new FakeEmit(t.Emit!.Url, t.Emit.Method)))
            .ToList();

        return new FakeManifest(fixtureDir, dto.InitialState, states, transitions);
    }

    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    private sealed record ManifestDto(
        [property: JsonPropertyName("initialState")] string InitialState,
        [property: JsonPropertyName("states")] Dictionary<string, StateDto> States,
        [property: JsonPropertyName("transitions")] List<TransitionDto> Transitions);

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
