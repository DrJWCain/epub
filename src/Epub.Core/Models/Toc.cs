namespace Epub.Core.Models;

public sealed record Toc(IReadOnlyList<TocNode> Nodes)
{
    public IEnumerable<TocNode> Flatten()
    {
        foreach (var node in Nodes)
        {
            yield return node;
            foreach (var descendant in node.Flatten())
                yield return descendant;
        }
    }
}

public sealed record TocNode
{
    public required string Title { get; init; }
    public string? Href { get; init; }
    public IReadOnlyList<TocNode> Children { get; init; } = Array.Empty<TocNode>();

    public IEnumerable<TocNode> Flatten()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var descendant in child.Flatten())
                yield return descendant;
        }
    }
}
