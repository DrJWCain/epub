using FluentAssertions;

namespace Epub.Core.Tests;

/// <summary>
/// Integration tests against the user's real EPUB library at C:\reading.
/// MemberData yields nothing when the directory is missing — tests are silently skipped on CI.
/// </summary>
public class IntegrationTests
{
    private const string LibraryPath = @"C:\reading";

    public static IEnumerable<object[]> RealEpubs()
    {
        if (!Directory.Exists(LibraryPath))
            yield break;

        foreach (var path in Directory.EnumerateFiles(LibraryPath, "*.epub"))
            yield return new object[] { path };
    }

    [Theory]
    [MemberData(nameof(RealEpubs))]
    public void Real_epub_opens_and_exposes_basic_metadata(string path)
    {
        using var reader = EpubReader.Open(path);

        reader.Book.Metadata.Title.Should().NotBeNullOrWhiteSpace(
            $"every real EPUB should have a title ({Path.GetFileName(path)})");
        reader.Book.Manifest.Should().NotBeEmpty(
            $"every real EPUB has manifest items ({Path.GetFileName(path)})");
        reader.Book.Spine.Should().NotBeEmpty(
            $"every real EPUB has a non-empty spine ({Path.GetFileName(path)})");
    }

    [Theory]
    [MemberData(nameof(RealEpubs))]
    public void Real_epub_first_spine_item_is_resolvable_and_readable(string path)
    {
        using var reader = EpubReader.Open(path);

        var firstItem = reader.Book.Spine[0].ManifestItem;
        var zipPath = reader.ResolveHref(firstItem.Href);
        reader.ResourceExists(zipPath).Should().BeTrue(
            $"first spine item must exist in zip ({Path.GetFileName(path)} -> {zipPath})");

        using var resource = reader.OpenResource(zipPath);
        using var sr = new StreamReader(resource);
        var content = sr.ReadToEnd();
        content.Length.Should().BeGreaterThan(0,
            $"first chapter should have content ({Path.GetFileName(path)})");
    }

    [Theory]
    [MemberData(nameof(RealEpubs))]
    public void Real_epub_has_resolvable_toc_or_explainable_absence(string path)
    {
        using var reader = EpubReader.Open(path);

        if (reader.Book.Toc is null)
            return; // Acceptable; test asserts the parser doesn't throw.

        reader.Book.Toc.Nodes.Should().NotBeEmpty(
            $"if a TOC exists it should have at least one entry ({Path.GetFileName(path)})");
    }
}
