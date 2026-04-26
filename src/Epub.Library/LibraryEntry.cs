namespace Epub.Library;

public sealed record LibraryEntry
{
    public required string FilePath { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();

    /// <summary>Raw bytes of the cover image, or null if the book has no cover.</summary>
    public byte[]? CoverImageBytes { get; init; }

    /// <summary>e.g., image/jpeg or image/png.</summary>
    public string? CoverImageMediaType { get; init; }
}
