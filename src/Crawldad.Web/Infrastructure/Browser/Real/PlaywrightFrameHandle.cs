using Microsoft.Playwright;

namespace Crawldad.Web.Infrastructure.Browser.Real;

/// <summary>A thin wrapper over a Playwright <see cref="IFrameLocator"/>. Like its page-level cousin it is lazy: the
/// returned <see cref="ILocatorHandle"/> re-queries the frame's current document on every terminal call, so a handle
/// obtained before an in-frame postback resolves the freshly rendered content afterward. Frames root CSS only.</summary>
internal sealed class PlaywrightFrameHandle(IFrameLocator frameLocator) : IFrameHandle
{
    public ILocatorHandle Locator(string selector) => new PlaywrightLocatorHandle(frameLocator.Locator(selector));
}
