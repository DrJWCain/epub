using System.IO.Compression;
using System.Text;

namespace Epub.Core.Tests.Fixtures;

/// <summary>
/// Builds in-memory EPUB archives for parser tests. Each fixture mimics a real-world publisher
/// pattern we want to be sure the parser handles.
/// </summary>
public static class TestFixtures
{
    public static MemoryStream MinimalEpub3()
    {
        return BuildEpub(
            ("META-INF/container.xml", Container("OEBPS/content.opf")),
            ("OEBPS/content.opf", Epub3Opf()),
            ("OEBPS/nav.xhtml", Epub3Nav()),
            ("OEBPS/ch01.xhtml", Chapter("Chapter 1", "First chapter body.")),
            ("OEBPS/ch02.xhtml", Chapter("Chapter 2", "Second chapter body.")));
    }

    public static MemoryStream MinimalEpub2()
    {
        return BuildEpub(
            ("META-INF/container.xml", Container("OEBPS/content.opf")),
            ("OEBPS/content.opf", Epub2Opf()),
            ("OEBPS/toc.ncx", Epub2Ncx()),
            ("OEBPS/ch01.html", Chapter("Chapter 1", "EPUB 2 chapter body.")));
    }

    /// <summary>Manning-style nested OPF directory.</summary>
    public static MemoryStream NestedPathsEpub()
    {
        return BuildEpub(
            ("META-INF/container.xml", Container("OEBPS/OEBPS/Text/content.opf")),
            ("OEBPS/OEBPS/Text/content.opf", Epub3Opf(navHref: "nav.xhtml", chapterHrefs: new[] { "ch01.xhtml" })),
            ("OEBPS/OEBPS/Text/nav.xhtml", Epub3Nav(chapterHrefs: new[] { "ch01.xhtml" })),
            ("OEBPS/OEBPS/Text/ch01.xhtml", Chapter("Nested Chapter", "Body in nested directory.")));
    }

    public static MemoryStream EpubWithEpub2Cover()
    {
        var opf = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package version="2.0" unique-identifier="bookid" xmlns="http://www.idpf.org/2007/opf">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Cover Test</dc:title>
                <dc:creator>Author Name</dc:creator>
                <dc:identifier id="bookid">urn:isbn:1234567890</dc:identifier>
                <dc:language>en</dc:language>
                <meta name="cover" content="cover-image"/>
              </metadata>
              <manifest>
                <item id="cover-image" href="cover.jpg" media-type="image/jpeg"/>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                <item id="ch1" href="ch01.html" media-type="application/xhtml+xml"/>
              </manifest>
              <spine toc="ncx">
                <itemref idref="ch1"/>
              </spine>
            </package>
            """;
        return BuildEpub(
            ("META-INF/container.xml", Container("OEBPS/content.opf")),
            ("OEBPS/content.opf", opf),
            ("OEBPS/cover.jpg", "fake-jpeg-bytes"),
            ("OEBPS/toc.ncx", Epub2Ncx()),
            ("OEBPS/ch01.html", Chapter("Cover Test", "Body.")));
    }

    public static MemoryStream EpubWithMultipleAuthors()
    {
        var opf = $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package version="3.0" unique-identifier="bookid" xmlns="http://www.idpf.org/2007/opf">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Many Authors</dc:title>
                <dc:creator>Alice</dc:creator>
                <dc:creator>Bob</dc:creator>
                <dc:creator>Carol</dc:creator>
                <dc:identifier id="bookid">urn:uuid:abc-123</dc:identifier>
                <dc:language>en</dc:language>
              </metadata>
              <manifest>
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="ch1" href="ch01.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine>
                <itemref idref="ch1"/>
              </spine>
            </package>
            """;
        return BuildEpub(
            ("META-INF/container.xml", Container("OEBPS/content.opf")),
            ("OEBPS/content.opf", opf),
            ("OEBPS/nav.xhtml", Epub3Nav(chapterHrefs: new[] { "ch01.xhtml" })),
            ("OEBPS/ch01.xhtml", Chapter("Ch 1", "Body.")));
    }

    private static MemoryStream BuildEpub(params (string Path, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Write mimetype first, uncompressed, per EPUB spec.
            var mimetypeEntry = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(mimetypeEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                writer.Write("application/epub+zip");

            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static string Container(string opfPath) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="{{opfPath}}" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    private static string Epub3Opf(string navHref = "nav.xhtml", string[]? chapterHrefs = null)
    {
        chapterHrefs ??= new[] { "ch01.xhtml", "ch02.xhtml" };
        var manifest = string.Join("\n    ",
            chapterHrefs.Select((h, i) => $"<item id=\"ch{i + 1}\" href=\"{h}\" media-type=\"application/xhtml+xml\"/>"));
        var spine = string.Join("\n    ",
            chapterHrefs.Select((_, i) => $"<itemref idref=\"ch{i + 1}\"/>"));

        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <package version="3.0" unique-identifier="bookid" xmlns="http://www.idpf.org/2007/opf">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>Test Book</dc:title>
                <dc:creator>Test Author</dc:creator>
                <dc:identifier id="bookid">urn:uuid:test-id</dc:identifier>
                <dc:language>en</dc:language>
                <dc:publisher>Test Publisher</dc:publisher>
                <dc:date>2026-01-01</dc:date>
              </metadata>
              <manifest>
                <item id="nav" href="{{navHref}}" media-type="application/xhtml+xml" properties="nav"/>
                {{manifest}}
              </manifest>
              <spine>
                {{spine}}
              </spine>
            </package>
            """;
    }

    private static string Epub2Opf() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <package version="2.0" unique-identifier="bookid" xmlns="http://www.idpf.org/2007/opf">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:title>EPUB 2 Test</dc:title>
            <dc:creator>Old School Author</dc:creator>
            <dc:identifier id="bookid">urn:isbn:9780000000001</dc:identifier>
            <dc:language>en</dc:language>
          </metadata>
          <manifest>
            <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
            <item id="ch1" href="ch01.html" media-type="application/xhtml+xml"/>
          </manifest>
          <spine toc="ncx">
            <itemref idref="ch1"/>
          </spine>
        </package>
        """;

    private static string Epub3Nav(string[]? chapterHrefs = null)
    {
        chapterHrefs ??= new[] { "ch01.xhtml", "ch02.xhtml" };
        var lis = string.Join("\n      ",
            chapterHrefs.Select((h, i) => $"<li><a href=\"{h}\">Chapter {i + 1}</a></li>"));

        return $$"""
            <?xml version="1.0" encoding="UTF-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
              <head><title>Contents</title></head>
              <body>
                <nav epub:type="toc">
                  <h1>Contents</h1>
                  <ol>
                    {{lis}}
                  </ol>
                </nav>
              </body>
            </html>
            """;
    }

    private static string Epub2Ncx() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
          <head>
            <meta name="dtb:uid" content="urn:isbn:9780000000001"/>
          </head>
          <docTitle><text>EPUB 2 Test</text></docTitle>
          <navMap>
            <navPoint id="navp1" playOrder="1">
              <navLabel><text>Chapter 1</text></navLabel>
              <content src="ch01.html"/>
            </navPoint>
          </navMap>
        </ncx>
        """;

    private static string Chapter(string title, string body) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
          <head><title>{{title}}</title></head>
          <body><h1>{{title}}</h1><p>{{body}}</p></body>
        </html>
        """;
}
