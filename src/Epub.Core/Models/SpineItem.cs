namespace Epub.Core.Models;

public sealed record SpineItem
{
    public required string IdRef { get; init; }
    public bool Linear { get; init; } = true;
    public required ManifestItem ManifestItem { get; init; }
}
