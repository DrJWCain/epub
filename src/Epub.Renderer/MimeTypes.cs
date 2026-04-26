namespace Epub_Renderer;

/// <summary>Fallback MIME lookup for resources that aren't in the OPF manifest (CSS-referenced fonts, etc.).</summary>
internal static class MimeTypes
{
    private static readonly Dictionary<string, string> _byExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".xhtml"] = "application/xhtml+xml",
        [".html"] = "application/xhtml+xml",
        [".htm"] = "application/xhtml+xml",
        [".css"] = "text/css",
        [".js"] = "application/javascript",
        [".mjs"] = "application/javascript",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".txt"] = "text/plain",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".otf"] = "font/otf",
        [".ttf"] = "font/ttf",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".eot"] = "application/vnd.ms-fontobject",
        [".mp3"] = "audio/mpeg",
        [".mp4"] = "video/mp4",
        [".m4a"] = "audio/mp4",
        [".opf"] = "application/oebps-package+xml",
        [".ncx"] = "application/x-dtbncx+xml",
    };

    public static string FromExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
            return "application/octet-stream";
        return _byExtension.TryGetValue(extension, out var mime) ? mime : "application/octet-stream";
    }
}
