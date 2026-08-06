using System.Security.Cryptography;
using System.Text;
using Crawldad.Web.Infrastructure.Storage;

namespace Crawldad.Tests.Unit;

/// <summary>
/// Pins the engine-native content identity (§9.3) to a golden vector, byte-for-byte with the reference
/// <c>AttachmentHashing</c>. The expected GUID is hand-derived from the SHA-256's first 16 bytes under
/// <c>new Guid(byte[16])</c>'s mixed-endian layout — bytes 0-3 / 4-5 / 6-7 little-endian, bytes 8-15 in order —
/// independent of the engine's own <c>new Guid(...)</c>, so it is a real pin, not a re-run of the same code.
/// </summary>
public class AttachmentContentIdTests
{
    // The SAME 30 bytes as Fixtures/download-sample/sample.bin ("Crawldad sample attachment v1\n").
    private static readonly byte[] _sampleBytes = Encoding.ASCII.GetBytes("Crawldad sample attachment v1\n");

    [Fact]
    public void Content_id_is_the_first_16_sha256_bytes_as_a_mixed_endian_guid()
    {
        var hash = SHA256.HashData(_sampleBytes);

        // Pinned: full hash, and the GUID its first 16 bytes yield under the Guid(byte[16]) byte order.
        Convert.ToHexStringLower(hash).ShouldBe("e22edc18626ec6f58ec1648aa28b2f48fc168b6ce9defa3b40344b1eb22f789e");
        AttachmentContentId.FromHash(hash).ToString().ShouldBe("18dc2ee2-6e62-f5c6-8ec1-648aa28b2f48");
    }

    [Fact]
    public void Content_id_equals_the_reference_new_guid_construction()
    {
        // AttachmentHashing.AttachmentIdFromHash = new Guid(sha256Hash.AsSpan(0,16).ToArray()); the span ctor is identical.
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes("a different payload entirely"));
        AttachmentContentId.FromHash(hash).ShouldBe(new Guid(hash.AsSpan(0, 16).ToArray()));
    }

    // BuildStoredName replicates AttachmentHashing.BuildInternalFilename on the download's suggested name: an extension
    // present (after the LAST dot) → "{id}.{ext}"; absent (no dot) or blank (a trailing dot) → the bare "{id}".
    [Theory]
    [InlineData("report.pdf", "{0}.pdf")]
    [InlineData("Site Photo.jpg", "{0}.jpg")]
    [InlineData("archive.tar.gz", "{0}.gz")]
    [InlineData("READMEnoext", "{0}")]
    [InlineData("trailingdot.", "{0}")]
    public void Stored_name_uses_the_suggested_filename_extension(string suggested, string expectedTemplate)
    {
        var id = Guid.Parse("18dc2ee2-6e62-f5c6-8ec1-648aa28b2f48");
        AttachmentContentId.BuildStoredName(id, suggested).ShouldBe(string.Format(null, expectedTemplate, id));
    }

    [Fact]
    public void Stored_name_with_a_null_suggested_filename_is_the_bare_id()
    {
        var id = Guid.NewGuid();
        AttachmentContentId.BuildStoredName(id, null).ShouldBe(id.ToString());
    }
}
