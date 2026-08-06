namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>
/// A handle to an <c>&lt;iframe&gt;</c>'s content (a Playwright <c>IFrameLocator</c>), bound by the <c>frame</c> node
/// (§5.1) and named by <c>in:</c> on actions and selectors (§5.2). Obtained from <see cref="IPageHandle.FrameLocator"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Frame handles are LAZY, exactly like Playwright's <c>FrameLocator</c>.</b> The handle captures which iframe to
/// look inside (a selector for the frame element), not a snapshot of the frame's document. <see cref="Locator"/> returns
/// an <see cref="ILocatorHandle"/> that — being itself lazy — resolves against the frame's <em>current</em> document on
/// every terminal call. The reference's attachments grid relies on this: after an in-frame pagination postback
/// re-renders the grid, a locator obtained from the frame before the click resolves the freshly rendered rows on next
/// use, with no rebind (LJCMGClient.cs:531-621).
/// </para>
/// <para>
/// Frames root CSS only — matching Playwright's <c>FrameLocator</c>, which exposes <c>Locator</c> but not the page-level
/// <c>GetByTitle</c> — so a <c>Sel</c> resolved inside a frame uses <c>css</c> (or a bound-handle <c>base</c>, which
/// carries its own frame context); <c>title</c> is a page-level root only.
/// </para>
/// </remarks>
public interface IFrameHandle
{
    /// <summary>Creates a lazy locator scoped to this frame's document (<c>frameLocator.Locator(css)</c>).</summary>
    /// <param name="selector">A CSS selector evaluated against the frame's current document on every terminal call.</param>
    ILocatorHandle Locator(string selector);
}
