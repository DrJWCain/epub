using Epub.Core.Tests.Fixtures;
using FluentAssertions;

namespace Epub.Core.Tests;

public class EpubReaderTests
{
    [Fact]
    public void Opens_minimal_epub3_and_extracts_metadata()
    {
        using var stream = TestFixtures.MinimalEpub3();
        using var reader = EpubReader.Open(stream);

        reader.Book.Metadata.Title.Should().Be("Test Book");
        reader.Book.Metadata.Authors.Should().ContainSingle().Which.Should().Be("Test Author");
        reader.Book.Metadata.Identifier.Should().Be("urn:uuid:test-id");
        reader.Book.Metadata.Language.Should().Be("en");
        reader.Book.Metadata.Publisher.Should().Be("Test Publisher");
        reader.Book.Metadata.PublicationDate.Should().Be("2026-01-01");
    }

    [Fact]
    public void Spine_yields_chapters_in_order()
    {
        using var stream = TestFixtures.MinimalEpub3();
        using var reader = EpubReader.Open(stream);

        reader.Book.Spine.Should().HaveCount(2);
        reader.Book.Spine[0].ManifestItem.Href.Should().Be("ch01.xhtml");
        reader.Book.Spine[1].ManifestItem.Href.Should().Be("ch02.xhtml");
        reader.Book.Spine.Should().AllSatisfy(s => s.Linear.Should().BeTrue());
    }

    [Fact]
    public void Toc_is_parsed_from_nav_for_epub3()
    {
        using var stream = TestFixtures.MinimalEpub3();
        using var reader = EpubReader.Open(stream);

        reader.Book.Toc.Should().NotBeNull();
        reader.Book.Toc!.Nodes.Should().HaveCount(2);
        reader.Book.Toc.Nodes[0].Title.Should().Be("Chapter 1");
        // TocNode.Href is now an absolute zip path (resolved relative to nav.xhtml location).
        reader.Book.Toc.Nodes[0].Href.Should().Be("OEBPS/ch01.xhtml");
    }

    [Fact]
    public void Toc_is_parsed_from_ncx_for_epub2()
    {
        using var stream = TestFixtures.MinimalEpub2();
        using var reader = EpubReader.Open(stream);

        reader.Book.Toc.Should().NotBeNull();
        reader.Book.Toc!.Nodes.Should().ContainSingle()
            .Which.Title.Should().Be("Chapter 1");
        reader.Book.Toc.Nodes[0].Href.Should().Be("OEBPS/ch01.html");
    }

    [Fact]
    public void Multiple_dc_creator_elements_are_all_extracted()
    {
        using var stream = TestFixtures.EpubWithMultipleAuthors();
        using var reader = EpubReader.Open(stream);

        reader.Book.Metadata.Authors.Should().Equal("Alice", "Bob", "Carol");
    }

    [Fact]
    public void Cover_image_resolves_via_epub2_meta_name_cover()
    {
        using var stream = TestFixtures.EpubWithEpub2Cover();
        using var reader = EpubReader.Open(stream);

        reader.Book.CoverImageHref.Should().Be("cover.jpg");
    }

    [Fact]
    public void Nested_opf_directory_resolves_paths_correctly()
    {
        using var stream = TestFixtures.NestedPathsEpub();
        using var reader = EpubReader.Open(stream);

        reader.Book.OpfPath.Should().Be("OEBPS/OEBPS/Text/content.opf");
        reader.Book.OpfBaseDir.Should().Be("OEBPS/OEBPS/Text");

        var resolved = reader.ResolveHref("ch01.xhtml");
        resolved.Should().Be("OEBPS/OEBPS/Text/ch01.xhtml");
        reader.ResourceExists(resolved).Should().BeTrue();
    }

    [Fact]
    public void OpenResource_reads_chapter_content()
    {
        using var stream = TestFixtures.MinimalEpub3();
        using var reader = EpubReader.Open(stream);

        var ch1Path = reader.ResolveHref(reader.Book.Spine[0].ManifestItem.Href);
        using var resource = reader.OpenResource(ch1Path);
        using var sr = new StreamReader(resource);
        var content = sr.ReadToEnd();

        content.Should().Contain("First chapter body.");
    }

    [Fact]
    public void OpenResource_throws_for_missing_path()
    {
        using var stream = TestFixtures.MinimalEpub3();
        using var reader = EpubReader.Open(stream);

        var act = () => reader.OpenResource("OEBPS/missing.xhtml");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Open_throws_for_archive_without_container()
    {
        using var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            // Empty archive — no META-INF/container.xml
        }
        ms.Position = 0;

        var act = () => EpubReader.Open(ms);
        act.Should().Throw<InvalidEpubException>().WithMessage("*container.xml*");
    }
}
