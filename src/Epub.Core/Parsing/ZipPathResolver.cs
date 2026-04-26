namespace Epub.Core.Parsing;

internal static class ZipPathResolver
{
    /// <summary>
    /// Resolve an OPF-relative href to a normalized zip-internal path (forward slashes, no "..").
    /// </summary>
    public static string Resolve(string opfBaseDir, string href)
    {
        ArgumentNullException.ThrowIfNull(href);

        // Strip URL fragment — hrefs in nav/NCX often include #anchor.
        var hashIndex = href.IndexOf('#');
        if (hashIndex >= 0)
            href = href[..hashIndex];

        href = Uri.UnescapeDataString(href).Replace('\\', '/');

        if (href.StartsWith('/'))
            return Normalize(href.TrimStart('/'));

        var combined = string.IsNullOrEmpty(opfBaseDir)
            ? href
            : opfBaseDir.TrimEnd('/') + "/" + href;

        return Normalize(combined);
    }

    /// <summary>Directory portion of a zip path (no trailing slash). Empty string if root.</summary>
    public static string DirectoryOf(string zipPath)
    {
        var slash = zipPath.LastIndexOf('/');
        return slash < 0 ? string.Empty : zipPath[..slash];
    }

    private static string Normalize(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return string.Join('/', segments);
    }
}
