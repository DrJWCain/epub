using System.Xml.Linq;

namespace Epub.Core.Parsing;

internal static class Namespaces
{
    public static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    public static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    public static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    public static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    public static readonly XNamespace EpubOps = "http://www.idpf.org/2007/ops";
    public static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";
}
