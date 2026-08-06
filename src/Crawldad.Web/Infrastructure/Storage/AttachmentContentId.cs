namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>
/// The engine-native content identity for a downloaded blob (§9.3/§9.4) — a byte-for-byte replica of the reference's
/// <c>MRR.Domain.US.KY.Jefferson.LJCMG.Enforcement.AttachmentHashing</c>, so a payload never constructs the GUID
/// itself. The <c>contentId</c> is the <b>first 16 bytes</b> of the stream's SHA-256 interpreted as a
/// <see cref="Guid"/>, exactly as <c>new Guid(sha256Hash.AsSpan(0,16).ToArray())</c> does: mixed-endian (the
/// <c>Guid(ReadOnlySpan&lt;byte&gt;)</c> constructor reads bytes 0-3 / 4-5 / 6-7 little-endian and bytes 8-15 in
/// order), which is what pins the golden GUID a given byte sequence yields.
/// </summary>
internal static class AttachmentContentId
{
    /// <summary>
    /// The content id: <c>new Guid</c> over the first 16 bytes of the SHA-256 hash — identical to
    /// <c>AttachmentHashing.AttachmentIdFromHash</c> (the <c>ReadOnlySpan</c> and <c>byte[]</c> <see cref="Guid"/>
    /// constructors interpret the bytes identically, so this is byte-for-byte the reference).
    /// </summary>
    /// <param name="sha256Hash">The full 32-byte SHA-256 hash of the downloaded bytes (only the first 16 are used).</param>
    /// <returns>The content-addressed GUID.</returns>
    public static Guid FromHash(ReadOnlySpan<byte> sha256Hash) => new(sha256Hash[..16]);

    /// <summary>
    /// The engine's stored-blob name — a replica of <c>AttachmentHashing.BuildInternalFilename</c> applied to the
    /// download's HTTP-<em>suggested</em> filename: <c>"{contentId}.{ext}"</c>, or bare <c>"{contentId}"</c> when the
    /// suggested name carries no extension. This is the physical name the sink stores under; it can legitimately differ
    /// from the payload's <c>internalFilename</c>, which the payload composes from the <em>scraped</em> filename cell
    /// (§9.3) — the two extensions need not agree.
    /// </summary>
    /// <param name="contentId">The content id from <see cref="FromHash"/>.</param>
    /// <param name="suggestedFilename">The download's suggested filename (may be null / extensionless).</param>
    /// <returns>The stored-blob name.</returns>
    public static string BuildStoredName(Guid contentId, string? suggestedFilename)
    {
        var extension = suggestedFilename is not null && suggestedFilename.Contains('.', StringComparison.Ordinal)
            ? suggestedFilename[(suggestedFilename.LastIndexOf('.') + 1)..]
            : "";
        return string.IsNullOrWhiteSpace(extension) ? contentId.ToString() : $"{contentId}.{extension}";
    }
}
