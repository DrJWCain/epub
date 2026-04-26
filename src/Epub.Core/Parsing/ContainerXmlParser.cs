using System.Xml.Linq;

namespace Epub.Core.Parsing;

internal static class ContainerXmlParser
{
    public const string ContainerPath = "META-INF/container.xml";

    /// <summary>Returns the OPF rootfile path (forward-slash zip path).</summary>
    public static string FindOpfPath(Stream containerXml)
    {
        var doc = XDocument.Load(containerXml);
        var rootfile = doc.Descendants(Namespaces.Container + "rootfile")
            .FirstOrDefault(r =>
                (string?)r.Attribute("media-type") == "application/oebps-package+xml")
            ?? doc.Descendants(Namespaces.Container + "rootfile").FirstOrDefault()
            ?? throw new InvalidEpubException("META-INF/container.xml has no <rootfile>.");

        var fullPath = (string?)rootfile.Attribute("full-path")
            ?? throw new InvalidEpubException("<rootfile> has no full-path attribute.");

        return fullPath.Replace('\\', '/');
    }
}
