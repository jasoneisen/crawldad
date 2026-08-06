namespace Crawldad.Web.Infrastructure.Browser.Fake;

/// <summary>
/// A record/replay frame handle (§ frames): a lazy <see cref="IFrameHandle"/> over the content of one iframe on the
/// current page state. Like Playwright's <c>FrameLocator</c> it captures only <em>which</em> iframe to look inside
/// (<paramref name="frameSelector"/>); every <see cref="Locator"/> returns a lazy <see cref="FakeLocatorHandle"/> that
/// re-queries the frame's <em>current</em> document on use — so after an in-frame postback swaps the state (and thus the
/// frame's HTML), a handle obtained before the swap resolves the freshly rendered grid, no rebind needed.
/// </summary>
/// <param name="page">The owning page (source of the frame's current document and the click sink).</param>
/// <param name="frameSelector">The iframe element's CSS selector, keying into the state's <c>frames</c> map.</param>
internal sealed class FakeFrameHandle(FakePageHandle page, string frameSelector) : IFrameHandle
{
    public ILocatorHandle Locator(string selector) => FakeLocatorHandle.InFrame(page, frameSelector, selector);
}
