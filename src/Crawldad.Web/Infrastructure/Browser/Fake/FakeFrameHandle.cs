namespace Crawldad.Web.Infrastructure.Browser.Fake;

/// <summary>A lazy record/replay frame handle over one iframe's content. Like Playwright's FrameLocator, every
/// <see cref="Locator"/> re-queries the frame's current document on use, so a handle obtained before an in-frame
/// postback swaps the state still resolves the freshly rendered content, no rebind needed.</summary>
internal sealed class FakeFrameHandle(FakePageHandle page, string frameSelector) : IFrameHandle
{
    public ILocatorHandle Locator(string selector) => FakeLocatorHandle.InFrame(page, frameSelector, selector);
}
