using Epub.Core.Parsing;
using FluentAssertions;

namespace Epub.Core.Tests;

public class ZipPathResolverTests
{
    [Theory]
    [InlineData("OEBPS", "ch01.xhtml", "OEBPS/ch01.xhtml")]
    [InlineData("OEBPS", "images/cover.jpg", "OEBPS/images/cover.jpg")]
    [InlineData("", "ch01.xhtml", "ch01.xhtml")]
    [InlineData("OEBPS/Text", "../Images/cover.jpg", "OEBPS/Images/cover.jpg")]
    [InlineData("OEBPS", "./ch01.xhtml", "OEBPS/ch01.xhtml")]
    [InlineData("OEBPS", "ch01.xhtml#section1", "OEBPS/ch01.xhtml")]
    [InlineData("OEBPS", "/META-INF/container.xml", "META-INF/container.xml")]
    [InlineData("OEBPS", @"images\cover.jpg", "OEBPS/images/cover.jpg")]
    [InlineData("OEBPS", "ch%2001.xhtml", "OEBPS/ch 01.xhtml")]
    public void Resolve_normalizes_paths(string baseDir, string href, string expected)
    {
        ZipPathResolver.Resolve(baseDir, href).Should().Be(expected);
    }

    [Theory]
    [InlineData("OEBPS/content.opf", "OEBPS")]
    [InlineData("OEBPS/OEBPS/Text/content.opf", "OEBPS/OEBPS/Text")]
    [InlineData("content.opf", "")]
    public void DirectoryOf_returns_directory_portion(string zipPath, string expected)
    {
        ZipPathResolver.DirectoryOf(zipPath).Should().Be(expected);
    }
}
