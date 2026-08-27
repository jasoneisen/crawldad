namespace Crawldad.Api.Infrastructure.Storage;

/// <summary>Guesses a content type from a stored-blob name's extension, for the <c>Downloaded</c> trace event's
/// <c>contentType</c> — a best-effort observability hint, not authoritative sniffing. Unknown extensions fall back
/// to <c>application/octet-stream</c>.</summary>
internal static class ContentTypes
{
    /// <summary>The screenshot media type (all captures are PNG) — the screenshot-retrieval response's content type.</summary>
    public const string Png = "image/png";

    /// <summary>Maps a file name's extension to a content type (defaulting to <c>application/octet-stream</c>).</summary>
    /// <param name="fileName">The stored-blob or suggested file name.</param>
    /// <returns>The guessed content type.</returns>
    public static string ForFile(string fileName) => Path.GetExtension(fileName).ToUpperInvariant() switch
    {
        ".PDF" => "application/pdf",
        ".JPG" or ".JPEG" => "image/jpeg",
        ".PNG" => Png,
        ".HTML" or ".HTM" => "text/html",
        ".CSV" => "text/csv",
        ".JSON" => "application/json",
        ".TXT" => "text/plain",
        _ => "application/octet-stream",
    };
}
