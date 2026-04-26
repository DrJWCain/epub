namespace Epub.Core.Models;

public sealed record Book
{
    public required Metadata Metadata { get; init; }
    public required IReadOnlyList<ManifestItem> Manifest { get; init; }
    public required IReadOnlyList<SpineItem> Spine { get; init; }
    public Toc? Toc { get; init; }

    /// <summary>Path of the OPF file inside the zip (forward slashes).</summary>
    public required string OpfPath { get; init; }

    /// <summary>Directory portion of OpfPath; manifest hrefs resolve against this.</summary>
    public required string OpfBaseDir { get; init; }

    /// <summary>Manifest item href of the cover image, if declared.</summary>
    public string? CoverImageHref { get; init; }
}
