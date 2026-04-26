namespace Epub.Core.Models;

public sealed record ManifestItem
{
    public required string Id { get; init; }
    public required string Href { get; init; }
    public required string MediaType { get; init; }
    public IReadOnlyList<string> Properties { get; init; } = Array.Empty<string>();

    public bool HasProperty(string name) =>
        Properties.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
}
