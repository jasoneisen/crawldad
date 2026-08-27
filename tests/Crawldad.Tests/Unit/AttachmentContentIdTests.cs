using System.Security.Cryptography;
using System.Text;
using Crawldad.Api.Infrastructure.Storage;

namespace Crawldad.Tests.Unit;

/// <summary>Pins the engine-native content identity to a golden vector, byte-for-byte with <c>AttachmentHashing</c>:
/// the GUID is hand-derived from SHA-256's first 16 bytes under <c>Guid(byte[16])</c>'s mixed-endian layout (bytes
/// 0-3/4-5/6-7 little-endian, 8-15 in order) — independent of the engine's own code, so it is a real pin.</summary>
public class AttachmentContentIdTests
{
    // The SAME 30 bytes as Fixtures/download-sample/sample.bin ("Crawldad sample attachment v1\n").
    private static readonly byte[] _sampleBytes = Encoding.ASCII.GetBytes("Crawldad sample attachment v1\n");

    [Fact]
    public void Content_id_is_the_first_16_sha256_bytes_as_a_mixed_endian_guid()
    {
        var hash = SHA256.HashData(_sampleBytes);

        Convert.ToHexStringLower(hash).ShouldBe("e22edc18626ec6f58ec1648aa28b2f48fc168b6ce9defa3b40344b1eb22f789e");
        AttachmentContentId.FromHash(hash).ToString().ShouldBe("18dc2ee2-6e62-f5c6-8ec1-648aa28b2f48");
    }

    [Fact]
    public void Content_id_equals_the_reference_new_guid_construction()
    {
        // Mirrors production's AttachmentHashing.AttachmentIdFromHash construction.
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
