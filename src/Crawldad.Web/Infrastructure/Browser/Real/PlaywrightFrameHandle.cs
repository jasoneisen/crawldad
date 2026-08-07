using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>
/// A thin wrapper over a Playwright <see cref="IFrameLocator"/> (§ frames). Like its page-level cousin it is lazy: the
/// returned <see cref="ILocatorHandle"/> re-queries the frame's current document on every terminal, so a handle
/// obtained before an in-frame postback resolves the freshly rendered grid afterward (the attachments traversal,
/// LJCMGClient.cs:531-621). Frames root CSS only, matching <see cref="IFrameHandle"/>.
/// </summary>
/// <param name="frameLocator">The wrapped Playwright frame locator (already lazy).</param>
internal sealed class PlaywrightFrameHandle(IFrameLocator frameLocator) : IFrameHandle
{
    public ILocatorHandle Locator(string selector) => new PlaywrightLocatorHandle(frameLocator.Locator(selector));
}
