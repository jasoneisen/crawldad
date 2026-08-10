namespace Crawldad.Web.Infrastructure.Storage;

/// <summary>The engine-native content identity for a downloaded blob — a byte-for-byte replica of the reference
/// implementation's hashing, so a payload never constructs the GUID itself. <c>contentId</c> is the first 16 bytes
/// of the SHA-256 as a <see cref="Guid"/>, mixed-endian per the <c>Guid(ReadOnlySpan&lt;byte&gt;)</c> constructor.</summary>
internal static class AttachmentContentId
{
    /// <summary>The content id: <c>new Guid</c> over the first 16 bytes of the SHA-256 hash — byte-for-byte identical
    /// to the reference's <c>AttachmentHashing.AttachmentIdFromHash</c>.</summary>
    public static Guid FromHash(ReadOnlySpan<byte> sha256Hash) => new(sha256Hash[..16]);

    /// <summary>The engine's stored-blob name — a replica of the reference's <c>AttachmentHashing.BuildInternalFilename</c>:
    /// <c>"{contentId}.{ext}"</c>, or bare <c>"{contentId}"</c> when the suggested name has no extension. This can
    /// legitimately differ from the payload's <c>internalFilename</c> (composed from the scraped filename cell).</summary>
    public static string BuildStoredName(Guid contentId, string? suggestedFilename)
    {
        var extension = suggestedFilename is not null && suggestedFilename.Contains('.', StringComparison.Ordinal)
            ? suggestedFilename[(suggestedFilename.LastIndexOf('.') + 1)..]
            : "";
        return string.IsNullOrWhiteSpace(extension) ? contentId.ToString() : $"{contentId}.{extension}";
    }
}
