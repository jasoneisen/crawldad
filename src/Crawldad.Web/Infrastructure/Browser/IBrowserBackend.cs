namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>The injected browser seam: an adapter establishes a live connection to a customer-supplied backend
/// (Browserless native, Browserbase over CDP, a self-hosted tunnel) and hands back an <see cref="IBrowserSession"/>.
/// Shaped like Playwright-for-.NET so real adapters map onto it 1:1; the fake implements it for deterministic tests.</summary>
public interface IBrowserBackend
{
    /// <summary>Establishes a live connection for the binding and returns a session to open pages on. A real adapter
    /// applies <paramref name="policy"/>'s launch/context/route settings; the fake ignores it (the interpreter still
    /// reads <see cref="SessionPolicy.DefaultTimeoutMs"/> itself for the fake path).</summary>
    Task<IBrowserSession> ConnectAsync(BackendBinding binding, SessionPolicy policy, CancellationToken ct);
}

/// <summary>One connected browser session. Owns the underlying connection: disposing it tears the backend session
/// down cleanly, which the cancellation/cleanup path relies on to avoid leaking a remote session. Per-run and never
/// shared across tenants.</summary>
public interface IBrowserSession : IAsyncDisposable
{
    /// <summary>The backend region this session runs in (a binding option or the provider's session-create response).
    /// Recorded for cache-locality (the asset cache is keyed within region); the fake reports <c>"fake"</c>.</summary>
    string Region { get; }

    /// <summary>The number of route-cache hits served to this session's pages so far (an asset already in the
    /// cross-run cache, so no origin fetch occurred). Backs <c>stats.cacheHits</c>; always 0 for the fake.</summary>
    int CacheHits { get; }

    /// <summary>Opens a fresh page/tab in this session.</summary>
    Task<IPageHandle> NewPageAsync(CancellationToken ct);
}

/// <summary>A handle to one page. The interpreter drives navigation, waits, and network synchronisation through it,
/// and obtains <see cref="ILocatorHandle"/>s for DOM interaction and reads.</summary>
public interface IPageHandle
{
    /// <summary>The page's current URL. Backs the <c>pageUrl()</c> expression builtin.</summary>
    string Url { get; }

    /// <summary>Navigates to <paramref name="url"/> (<c>page.GotoAsync</c>).</summary>
    /// <param name="waitUntil">Playwright load state to await (<c>load</c>/<c>domcontentloaded</c>/<c>networkidle</c>/<c>commit</c>), or null for the backend default.</param>
    /// <param name="timeoutMs">Per-call timeout override in milliseconds, or null for the timeout hierarchy default.</param>
    Task GotoAsync(string url, string? waitUntil, int? timeoutMs, CancellationToken ct);

    /// <summary>Waits for a page-wide load state (<c>WaitForLoadStateAsync</c>).</summary>
    /// <param name="state">The load state to await (<c>load</c>/<c>domcontentloaded</c>/<c>networkidle</c>).</param>
    /// <param name="timeoutMs">Per-call timeout override in milliseconds, or null for the default.</param>
    Task WaitForLoadStateAsync(string state, int? timeoutMs, CancellationToken ct);

    /// <summary>Creates a page-scoped locator (<c>page.Locator</c>). <paramref name="selector"/> is CSS by default; an
    /// <c>"xpath=…"</c> prefix selects XPath, and comma-union CSS (<c>"#a, #b"</c>) passes through verbatim. The
    /// returned handle is lazy — see <see cref="ILocatorHandle"/>.</summary>
    ILocatorHandle Locator(string selector);

    /// <summary>Creates a locator matching by the element's <c>title</c> attribute (<c>page.GetByTitle</c>), lazily.</summary>
    /// <param name="title">The title text to match.</param>
    ILocatorHandle GetByTitle(string title);

    /// <summary>Creates a locator matching elements by ARIA <paramref name="role"/>, optionally narrowed to those
    /// whose accessible name contains <paramref name="name"/> (case-insensitive substring). Lazy; a page-level root,
    /// not frame-scoped.</summary>
    ILocatorHandle GetByRole(string role, string? name);

    /// <summary>Creates a locator matching elements by text content: case-insensitive, whitespace-normalised,
    /// substring match, resolving the innermost element carrying the text. Lazy; a page-level root, not
    /// frame-scoped.</summary>
    ILocatorHandle GetByText(string text);

    /// <summary>Binds a handle to the content of the iframe matched by <paramref name="selector"/>. The returned
    /// handle is lazy — see <see cref="IFrameHandle"/>.</summary>
    IFrameHandle FrameLocator(string selector);

    /// <summary>Injects a <c>&lt;style&gt;</c> tag carrying <paramref name="content"/> into the page. This is payload
    /// data, not executable code.</summary>
    Task AddStyleTagAsync(string content, CancellationToken ct);

    /// <summary>Runs <paramref name="trigger"/> and waits for a matching network request — the postback-synchronisation
    /// primitive: the wait is armed before the trigger fires, so a request the trigger causes is never missed.</summary>
    /// <param name="urlPrefix">Match requests whose URL starts with this prefix.</param>
    Task RunAndWaitForRequestAsync(Func<Task> trigger, string urlPrefix, string? method, int? timeoutMs, CancellationToken ct);

    /// <summary>Runs <paramref name="trigger"/> and waits for the download it provokes — the download analogue of
    /// <see cref="RunAndWaitForRequestAsync"/>: the wait is armed before the trigger fires, so the download is never
    /// missed. No download within the timeout is a retryable <see cref="BrowserTimeoutException"/>.</summary>
    Task<IDownloadHandle> RunAndWaitForDownloadAsync(Func<Task> trigger, int? timeoutMs, CancellationToken ct);

    /// <summary>Closes this page. Used by the page-crash reopen path: the crashed page is closed before a fresh one
    /// opens on the same session/context. A crashed page may itself fail to close — real adapters surface that as a
    /// <see cref="BrowserException"/>, which the reopen path tolerates — so this is best-effort, not guaranteed.</summary>
    Task CloseAsync(CancellationToken ct);

    /// <summary>Captures a full-page screenshot as PNG bytes, called on a step failure and streamed to blob storage.
    /// Best-effort: a crashed/torn-down page may throw a <see cref="BrowserException"/>, which the capture path
    /// tolerates — a failed screenshot never masks the run's real failure. The fake serves deterministic bytes.</summary>
    Task<byte[]> ScreenshotAsync(CancellationToken ct);

    /// <summary>Serialises the page's full document — the complete DOM including the doctype and the <c>&lt;html&gt;</c>
    /// element itself (<c>page.content()</c>), NOT the inner HTML of <c>html</c> — for a <c>capture</c> node and for
    /// capture-on-failure. The bytes stream content-addressed to a tenant BYO storage target and never route through the
    /// credential scrubber. On a step failure it is best-effort: a crashed page may throw a <see cref="BrowserException"/>,
    /// tolerated so a failed capture never masks the run's real failure. The fake serialises its fixture document.</summary>
    Task<string> ContentAsync(CancellationToken ct);
}

/// <summary>A handle to one completed download. The engine reads the bytes to compute content identity and streams
/// them to the sink; it never round-trips through the caller. Temp-file lifecycle (Playwright's own concern) stays
/// adapter-internal — the fake serves bytes from memory.</summary>
public interface IDownloadHandle
{
    /// <summary>The download's HTTP-suggested filename (<c>download.SuggestedFilename</c>) — the source of the engine's stored-blob extension.</summary>
    string SuggestedFilename { get; }

    /// <summary>Opens a fresh readable stream over the downloaded bytes (positioned at the start). The caller disposes it.</summary>
    Task<Stream> OpenReadAsync(CancellationToken ct);
}
