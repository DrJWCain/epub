namespace Epub.Core.Models;

public sealed record Metadata
{
    public required string Title { get; init; }
    public required IReadOnlyList<string> Authors { get; init; }
    public string? Publisher { get; init; }
    public string? Language { get; init; }
    public string? Identifier { get; init; }
    public string? PublicationDate { get; init; }
    public string? Description { get; init; }
}
