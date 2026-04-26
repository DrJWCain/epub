using System.Xml;
using System.Xml.Linq;
using Epub.Core.Models;

namespace Epub.Core.Parsing;

internal static class NavParser
{
    public static Toc Parse(Stream navXhtml)
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

        return new Toc(ParseList(rootList));
    }

    private static List<TocNode> ParseList(XElement listEl)
    {
        var nodes = new List<TocNode>();
        foreach (var li in listEl.Elements(Namespaces.Xhtml + "li"))
        {
            var anchor = li.Element(Namespaces.Xhtml + "a");
            var span = li.Element(Namespaces.Xhtml + "span");

            var title = (anchor ?? span)?.Value.Trim() ?? string.Empty;
            var href = (string?)anchor?.Attribute("href");

            var nestedList = li.Element(Namespaces.Xhtml + "ol") ?? li.Element(Namespaces.Xhtml + "ul");
            var children = nestedList is not null ? ParseList(nestedList) : new List<TocNode>();

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
}
