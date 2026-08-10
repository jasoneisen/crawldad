namespace Crawldad.Web.Infrastructure.Browser;

/// <summary>A handle to an iframe's content. Like Playwright's FrameLocator it is lazy: it captures which iframe to
/// look inside, and every <see cref="Locator"/> re-queries that frame's current document on every terminal call, so
/// a handle obtained before an in-frame postback still resolves the freshly rendered content. Frames root CSS only.</summary>
public interface IFrameHandle
{
    /// <summary>Creates a lazy locator scoped to this frame's document.</summary>
    ILocatorHandle Locator(string selector);
}
