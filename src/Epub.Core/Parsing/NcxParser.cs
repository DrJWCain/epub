using System.Xml;
using System.Xml.Linq;
using Epub.Core.Models;

namespace Epub.Core.Parsing;

internal static class NcxParser
{
    public static Toc Parse(Stream ncxXml)
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

        return new Toc(ParseNavPoints(navMap));
    }

    private static List<TocNode> ParseNavPoints(XElement parent)
    {
        var nodes = new List<TocNode>();
        foreach (var navPoint in parent.Elements(Namespaces.Ncx + "navPoint"))
        {
            var title = navPoint.Element(Namespaces.Ncx + "navLabel")?
                .Element(Namespaces.Ncx + "text")?.Value.Trim() ?? string.Empty;
            var href = (string?)navPoint.Element(Namespaces.Ncx + "content")?.Attribute("src");

            var children = ParseNavPoints(navPoint);

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
