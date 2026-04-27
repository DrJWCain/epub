using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Epub.Search;

/// <summary>
/// Extracts the plain-text content of an EPUB spine item with character offsets that
/// the in-WebView2 reader.js DOM walker can reproduce byte-for-byte. This alignment
/// is the contract that lets a search hit's (spine_idx, char_offset) coordinates open
/// the reader at the right paragraph.
/// </summary>
/// <remarks>
/// Offset contract (must match reader.js):
///   - Text nodes contribute their value verbatim. Whitespace is preserved exactly.
///   - script, style, and comment subtrees are skipped entirely.
///   - Block-level elements emit a single '\n' when they close.
///   - Inline elements contribute nothing beyond their descendants' text.
/// Lives in Epub.Search rather than Epub.Core because indexing is its only consumer
/// and the AngleSharp dependency stays scoped here. Lift back to Core if a non-search
/// use case (e.g. plaintext export) ever appears.
/// </remarks>
public static class SpineTextExtractor
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6",
        "li", "blockquote", "pre", "td", "th", "tr", "br",
        "section", "article", "aside", "figure", "figcaption", "hr",
    };

    /// <summary>Subset of <see cref="BlockTags"/> that the chunker treats as paragraph
    /// boundaries. Recorded only when no descendant paragraph was already recorded — keeps
    /// nested wrappers (e.g. blockquote &gt; p) from double-counting.</summary>
    private static readonly HashSet<string> ParagraphTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6",
        "li", "blockquote", "pre", "td", "th", "figcaption",
    };

    public static async Task<SpineText> ExtractAsync(Stream xhtmlStream, CancellationToken ct = default)
    {
        var ctx = BrowsingContext.New(Configuration.Default);
        var parser = ctx.GetService<IHtmlParser>()
            ?? throw new InvalidOperationException("AngleSharp HTML parser service unavailable.");
        using var doc = await parser.ParseDocumentAsync(xhtmlStream, ct).ConfigureAwait(false);
        return Extract(doc);
    }

    public static SpineText Extract(IDocument document)
    {
        var sb = new StringBuilder(8192);
        var paragraphs = new List<ParagraphSpan>();

        var body = document.Body;
        if (body is null) return new SpineText(string.Empty, Array.Empty<ParagraphSpan>());

        foreach (var child in body.ChildNodes)
            Walk(child, sb, paragraphs);

        return new SpineText(sb.ToString(), paragraphs);
    }

    private static bool Walk(INode node, StringBuilder sb, List<ParagraphSpan> paragraphs)
    {
        if (node.NodeType == NodeType.Text)
        {
            sb.Append(node.NodeValue ?? string.Empty);
            return false;
        }
        if (node.NodeType == NodeType.Comment) return false;

        if (node is not IElement elem)
        {
            bool any = false;
            foreach (var child in node.ChildNodes) any |= Walk(child, sb, paragraphs);
            return any;
        }

        var tag = elem.LocalName;
        if (string.Equals(tag, "script", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tag, "style", StringComparison.OrdinalIgnoreCase))
            return false;

        int startOffset = sb.Length;
        bool descendantRecorded = false;
        foreach (var child in elem.ChildNodes)
            descendantRecorded |= Walk(child, sb, paragraphs);

        if (BlockTags.Contains(tag)) sb.Append('\n');

        bool recordedHere = false;
        if (ParagraphTags.Contains(tag) && !descendantRecorded)
        {
            int length = sb.Length - startOffset;
            if (length > 0 && HasNonWhitespace(sb, startOffset, length))
            {
                paragraphs.Add(new ParagraphSpan(startOffset, length, elem.GetAttribute("id") ?? string.Empty));
                recordedHere = true;
            }
        }
        return descendantRecorded || recordedHere;
    }

    private static bool HasNonWhitespace(StringBuilder sb, int offset, int length)
    {
        for (int i = offset; i < offset + length; i++)
            if (!char.IsWhiteSpace(sb[i])) return true;
        return false;
    }
}

public sealed record SpineText(string Text, IReadOnlyList<ParagraphSpan> Paragraphs);

public readonly record struct ParagraphSpan(int CharOffset, int CharLength, string ElementId);
