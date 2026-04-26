using System.Xml;
using System.Xml.Linq;
using Epub.Core.Models;

namespace Epub.Core.Parsing;

internal static class NavParser
{
    /// <summary>
    /// Parse an EPUB 3 nav.xhtml. <paramref name="navBaseDir"/> is the zip directory containing
    /// the nav file — hrefs in nav are relative to it, and we resolve them to absolute zip
    /// paths so callers can match against spine items without further resolution.
    /// </summary>
    public static Toc Parse(Stream navXhtml, string navBaseDir)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(navXhtml, settings);
        var doc = XDocument.Load(reader);

        // Find <nav epub:type="toc">, falling back to first <nav>.
        var navs = doc.Descendants(Namespaces.Xhtml + "nav").ToList();
        var tocNav = navs.FirstOrDefault(n =>
            string.Equals((string?)n.Attribute(Namespaces.EpubOps + "type"), "toc", StringComparison.OrdinalIgnoreCase))
            ?? navs.FirstOrDefault()
            ?? throw new InvalidEpubException("nav.xhtml has no <nav> element.");

        var rootList = tocNav.Element(Namespaces.Xhtml + "ol")
            ?? tocNav.Element(Namespaces.Xhtml + "ul");
        if (rootList is null)
            return new Toc(Array.Empty<TocNode>());

        return new Toc(ParseList(rootList, navBaseDir));
    }

    private static List<TocNode> ParseList(XElement listEl, string navBaseDir)
    {
        var nodes = new List<TocNode>();
        foreach (var li in listEl.Elements(Namespaces.Xhtml + "li"))
        {
            var anchor = li.Element(Namespaces.Xhtml + "a");
            var span = li.Element(Namespaces.Xhtml + "span");

            var title = (anchor ?? span)?.Value.Trim() ?? string.Empty;
            var rawHref = (string?)anchor?.Attribute("href");
            var href = ResolveTocHref(rawHref, navBaseDir);

            var nestedList = li.Element(Namespaces.Xhtml + "ol") ?? li.Element(Namespaces.Xhtml + "ul");
            var children = nestedList is not null ? ParseList(nestedList, navBaseDir) : new List<TocNode>();

            if (title.Length == 0 && href is null && children.Count == 0)
                continue;

            nodes.Add(new TocNode
            {
                Title = title,
                Href = href,
                Children = children,
            });
        }
        return nodes;
    }

    private static string? ResolveTocHref(string? rawHref, string navBaseDir)
    {
        if (rawHref is null) return null;
        var hashIdx = rawHref.IndexOf('#');
        var pathPart = hashIdx >= 0 ? rawHref[..hashIdx] : rawHref;
        var fragment = hashIdx >= 0 ? rawHref[hashIdx..] : string.Empty;
        if (pathPart.Length == 0) return rawHref; // pure-fragment link
        return ZipPathResolver.Resolve(navBaseDir, pathPart) + fragment;
    }
}
