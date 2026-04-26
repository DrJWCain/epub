using System.Xml;
using System.Xml.Linq;
using Epub.Core.Models;

namespace Epub.Core.Parsing;

internal static class NcxParser
{
    /// <summary>
    /// Parse an EPUB 2 toc.ncx. <paramref name="ncxBaseDir"/> is the zip directory containing
    /// the NCX file — content/@src values are relative to it, resolved to absolute zip paths.
    /// </summary>
    public static Toc Parse(Stream ncxXml, string ncxBaseDir)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(ncxXml, settings);
        var doc = XDocument.Load(reader);

        var navMap = doc.Root?.Element(Namespaces.Ncx + "navMap")
            ?? throw new InvalidEpubException("toc.ncx has no <navMap>.");

        return new Toc(ParseNavPoints(navMap, ncxBaseDir));
    }

    private static List<TocNode> ParseNavPoints(XElement parent, string ncxBaseDir)
    {
        var nodes = new List<TocNode>();
        foreach (var navPoint in parent.Elements(Namespaces.Ncx + "navPoint"))
        {
            var title = navPoint.Element(Namespaces.Ncx + "navLabel")?
                .Element(Namespaces.Ncx + "text")?.Value.Trim() ?? string.Empty;
            var rawHref = (string?)navPoint.Element(Namespaces.Ncx + "content")?.Attribute("src");
            var href = ResolveTocHref(rawHref, ncxBaseDir);

            var children = ParseNavPoints(navPoint, ncxBaseDir);

            nodes.Add(new TocNode
            {
                Title = title,
                Href = href,
                Children = children,
            });
        }
        return nodes;
    }

    private static string? ResolveTocHref(string? rawHref, string ncxBaseDir)
    {
        if (rawHref is null) return null;
        var hashIdx = rawHref.IndexOf('#');
        var pathPart = hashIdx >= 0 ? rawHref[..hashIdx] : rawHref;
        var fragment = hashIdx >= 0 ? rawHref[hashIdx..] : string.Empty;
        if (pathPart.Length == 0) return rawHref;
        return ZipPathResolver.Resolve(ncxBaseDir, pathPart) + fragment;
    }
}
