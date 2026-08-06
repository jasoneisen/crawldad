namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>
/// The injected browser seam — the service never owns browsers (§9). An adapter establishes a live connection to
/// a customer-supplied backend (Browserless native, Browserbase over CDP, a self-hosted tunnel) and hands back an
/// <see cref="IBrowserSession"/>. A <c>FakeBrowserBackend</c> implements this for tests (record/replay of captured
/// DOM), so the whole interpreter runs deterministically with no Chromium. Phase 4 adds the real adapters; the
/// shape here is deliberately Playwright-for-.NET-like so those adapters map 1:1.
/// </summary>
public interface IBrowserBackend
{
    /// <summary>Establishes a live connection for the binding and returns a session to open pages on.</summary>
    /// <param name="binding">Adapter id + credential reference + provider options (§9.1).</param>
    /// <param name="ct">Cancels the connect attempt.</param>
    Task<IBrowserSession> ConnectAsync(BackendBinding binding, CancellationToken ct);
}

/// <summary>
/// One connected browser session (a Playwright <c>IBrowser</c>/<c>IBrowserContext</c> pair). Owns the underlying
/// connection: disposing it tears the backend session down cleanly — the cancellation and cleanup path relies on
/// this to avoid leaking a remote browser session (§11). Per-run and never shared across tenants (§12).
/// </summary>
public interface IBrowserSession : IAsyncDisposable
{
    /// <summary>Opens a fresh page/tab in this session.</summary>
    /// <param name="ct">Cancels opening the page.</param>
    Task<IPageHandle> NewPageAsync(CancellationToken ct);
}

/// <summary>
/// A handle to one page (a Playwright <c>IPage</c>). The interpreter drives navigation, waits, and network
/// synchronisation through it, and obtains <see cref="ILocatorHandle"/>s for DOM interaction and reads.
/// </summary>
public interface IPageHandle
{
    /// <summary>The page's current URL. Backs the <c>pageUrl()</c> expression builtin.</summary>
    string Url { get; }

    /// <summary>Navigates to <paramref name="url"/> (<c>page.GotoAsync</c>).</summary>
    /// <param name="url">Absolute URL to navigate to.</param>
    /// <param name="waitUntil">Playwright load state to await (<c>load</c>/<c>domcontentloaded</c>/<c>networkidle</c>/<c>commit</c>), or null for the backend default.</param>
    /// <param name="timeoutMs">Per-call timeout override in milliseconds, or null for the timeout hierarchy default (§8.4).</param>
    /// <param name="ct">Cancels the navigation.</param>
    Task GotoAsync(string url, string? waitUntil, int? timeoutMs, CancellationToken ct);

    /// <summary>Waits for a page-wide load state (<c>WaitForLoadStateAsync</c>).</summary>
    /// <param name="state">The load state to await (<c>load</c>/<c>domcontentloaded</c>/<c>networkidle</c>).</param>
    /// <param name="timeoutMs">Per-call timeout override in milliseconds, or null for the default.</param>
    /// <param name="ct">Cancels the wait.</param>
    Task WaitForLoadStateAsync(string state, int? timeoutMs, CancellationToken ct);

    /// <summary>
    /// Creates a page-scoped locator (<c>page.Locator</c>). <paramref name="selector"/> is CSS by default;
    /// a <c>"xpath=…"</c> prefix is reserved for XPath passthrough (Playwright-style), and comma-union CSS
    /// (<c>"#a, #b"</c>) is passed through verbatim. The returned handle is <b>lazy</b> — see the remarks on
    /// <see cref="ILocatorHandle"/>.
    /// </summary>
    /// <param name="selector">A CSS selector by default; <c>"xpath=…"</c> for XPath.</param>
    ILocatorHandle Locator(string selector);

    /// <summary>Creates a locator matching by the element's <c>title</c> attribute (<c>page.GetByTitle</c>), lazily.</summary>
    /// <param name="title">The title text to match.</param>
    ILocatorHandle GetByTitle(string title);

    /// <summary>
    /// Runs <paramref name="trigger"/> and waits for a matching network request to be issued
    /// (<c>RunAndWaitForRequestAsync</c>) — the postback-synchronisation primitive: the wait is armed before the
    /// trigger fires, so a request the trigger causes is never missed.
    /// </summary>
    /// <param name="trigger">The action that provokes the request (typically a click), awaited inside the wait window.</param>
    /// <param name="urlPrefix">Match requests whose URL starts with this prefix.</param>
    /// <param name="method">Optional HTTP method filter (e.g. <c>"POST"</c>), or null to match any method.</param>
    /// <param name="timeoutMs">Per-call timeout override in milliseconds, or null for the default.</param>
    /// <param name="ct">Cancels the trigger-and-wait.</param>
    Task RunAndWaitForRequestAsync(Func<Task> trigger, string urlPrefix, string? method, int? timeoutMs, CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="trigger"/> and waits for the download it provokes (<c>page.RunAndWaitForDownloadAsync</c>) —
    /// the download analogue of <see cref="RunAndWaitForRequestAsync"/>: the wait is armed before the trigger click
    /// fires, so the download event is never missed. Backs the <c>download</c> node (§9.3). A trigger that starts no
    /// download within the timeout is a retryable <see cref="BrowserTimeoutException"/> (the reference's 180 s
    /// <c>WaitForDownloadAsync</c> timeout).
    /// </summary>
    /// <param name="trigger">The action that starts the download (typically a click), awaited inside the wait window.</param>
    /// <param name="timeoutMs">Per-call timeout override in milliseconds, or null for the default (the node sets 180000).</param>
    /// <param name="ct">Cancels the trigger-and-wait.</param>
    /// <returns>A handle to the downloaded bytes and their suggested filename.</returns>
    Task<IDownloadHandle> RunAndWaitForDownloadAsync(Func<Task> trigger, int? timeoutMs, CancellationToken ct);

    /// <summary>
    /// Closes this page (<c>page.CloseAsync</c>). Used by the §3.6 page-crash reopen path: the crashed page is closed
    /// before a fresh one is opened on the same session/context. A crashed page may itself fail to close — real
    /// adapters surface that as a <see cref="BrowserException"/>, which the reopen path tolerates — so this is a
    /// best-effort teardown, not a guaranteed clean close.
    /// </summary>
    /// <param name="ct">Cancels the close.</param>
    Task CloseAsync(CancellationToken ct);
}

/// <summary>
/// A handle to one completed download (a Playwright <c>IDownload</c>). The engine reads the bytes to compute the
/// content identity (§9.3) and streams them to the sink; it never round-trips through the caller. The temp-file
/// lifecycle the reference manages by hand (<c>PathAsync</c>/<c>DeleteAsync</c>) is an adapter-internal concern in
/// Phase 2 — the fake serves bytes from memory.
/// </summary>
public interface IDownloadHandle
{
    /// <summary>The download's HTTP-suggested filename (<c>download.SuggestedFilename</c>) — the source of the engine's stored-blob extension (§9.3).</summary>
    string SuggestedFilename { get; }

    /// <summary>Opens a fresh readable stream over the downloaded bytes (positioned at the start). The caller disposes it.</summary>
    /// <param name="ct">Cancels opening the stream.</param>
    /// <returns>A readable stream over the bytes.</returns>
    Task<Stream> OpenReadAsync(CancellationToken ct);
}
