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
}
