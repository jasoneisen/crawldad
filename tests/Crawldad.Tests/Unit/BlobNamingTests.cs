using Crawldad.Web.Infrastructure.Storage;

namespace Crawldad.Tests.Unit;

/// <summary>The shared blob-naming guard (<see cref="BlobNaming"/>) both durable adapters route through: a safe
/// tenant/key segment passes unchanged, while a hostile one (empty, a separator, or <c>..</c>) is rejected — so no
/// tenant id can escape its prefix and reach another tenant's blobs.</summary>
public class BlobNamingTests
{
    [Theory]
    [InlineData("tenant-alpha")]
    [InlineData("t-0123abcdef")]
    public void A_safe_segment_passes_through(string value) => BlobNaming.SafeSegment(value).ShouldBe(value);

    [Theory]
    [InlineData("")]                       // empty
    [InlineData("..")]                      // parent traversal
    [InlineData("../victim")]               // traversal into a sibling
    [InlineData("a/b")]                     // forward-slash separator
    [InlineData("a\\b")]                    // back-slash separator
    [InlineData("evil/../other-tenant")]    // separator + traversal collapsing a prefix
    public void A_hostile_segment_is_rejected(string value) =>
        Should.Throw<ArgumentException>(() => BlobNaming.SafeSegment(value));

    [Theory]
    [InlineData(BlobKind.Download, "downloads")]
    [InlineData(BlobKind.Screenshot, "screenshots")]
    public void SubDir_maps_each_category(BlobKind kind, string expected) => BlobNaming.SubDir(kind).ShouldBe(expected);

    [Fact]
    public void A_well_formed_screenshot_ref_yields_its_digest()
    {
        var digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"; // spans both hex classes (digits + a–f)
        BlobNaming.TryParseScreenshotRef($"screenshots/{digest}.png", out var parsed).ShouldBeTrue();
        parsed.ShouldBe(digest);
    }

    [Theory]
    [InlineData(null)]                                   // null
    [InlineData("")]                                     // empty
    [InlineData("downloads/aaaa.png")]                   // wrong category prefix
    [InlineData("screenshots/aaaa.jpg")]                 // wrong extension
    [InlineData("screenshots/AAAA…too-short.png")]       // wrong length / non-hex
    [InlineData("screenshots/../escape.png")]            // traversal shape
    [InlineData("screenshots/0000000000000000000000000000000000000000000000000000000000000000")] // no .png suffix
    [InlineData("screenshots/ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789.png")] // uppercase hex rejected
    public void A_malformed_screenshot_ref_is_rejected(string? reference)
    {
        BlobNaming.TryParseScreenshotRef(reference, out var parsed).ShouldBeFalse();
        parsed.ShouldBeEmpty();
    }
}
