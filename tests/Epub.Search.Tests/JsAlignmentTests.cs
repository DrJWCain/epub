using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Epub.Search;
using FluentAssertions;

namespace Epub.Search.Tests;

/// <summary>
/// Pinned regression suite for the offset contract shared between
/// <see cref="SpineTextExtractor"/> (C#) and reader.js's <c>findNodeAtOffset</c> walker.
/// We can't run real Chromium DOM in a unit test, but we CAN port the JS walker
/// logic to C# operating on AngleSharp's DOM and assert it produces byte-identical
/// text and equivalent node resolution. If either implementation drifts, this
/// test catches it before search-result navigation lands on the wrong paragraph.
/// </summary>
public sealed class JsAlignmentTests
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6",
        "li", "blockquote", "pre", "td", "th", "tr", "br",
        "section", "article", "aside", "figure", "figcaption", "hr",
    };

    public static IEnumerable<object[]> AlignmentInputs() => new[]
    {
        new object[] { "<html><body><p>simple</p></body></html>" },
        new object[] { "<html><body><h1>Title</h1><p>First.</p><p>Second.</p></body></html>" },
        new object[] { "<html><body><div><p>Nested</p></div></body></html>" },
        new object[] { "<html><body><blockquote><p>Inner</p></blockquote></body></html>" },
        new object[] { "<html><body><p>before</p><script>x = 1</script><style>p{}</style><p>after</p></body></html>" },
        new object[] { "<html><body><p>line1<br/>line2</p></body></html>" },
        new object[] { "<html><body><p>nbsp&nbsp;here&mdash;and&amp;here</p></body></html>" },
        new object[] { "<html><body><ul><li>A</li><li>B</li></ul></body></html>" },
        new object[] { "<html><body><p>before</p><!-- comment --><p>after</p></body></html>" },
        new object[] { "<html><body><pre>code\nblock</pre></body></html>" },
        new object[] { "<html><body><p>preserve   spaces  </p></body></html>" },
        new object[] { "<html><body><figure><img src=\"x\"/><figcaption>caption</figcaption></figure></body></html>" },
    };

    [Theory]
    [MemberData(nameof(AlignmentInputs))]
    public async Task ExtractorAndJsStyleWalker_ProduceIdenticalText(string xhtml)
    {
        var spineText = await ExtractAsync(xhtml);
        var jsStyle = await JsStyleTextAsync(xhtml);

        jsStyle.Should().Be(spineText.Text,
            $"reader.js's text accumulation must match SpineTextExtractor exactly");
    }

    [Fact]
    public async Task FindNodeAtOffset_ResolvesEachParagraphSpan_ToItsElement()
    {
        const string xhtml =
            "<html><body>" +
            "<h1 id=\"a\">Hello</h1>" +
            "<p id=\"b\">First paragraph.</p>" +
            "<p id=\"c\">Second.</p>" +
            "</body></html>";

        var spineText = await ExtractAsync(xhtml);
        var doc = await ParseAsync(xhtml);

        // Property: for every recorded ParagraphSpan, finding offset == span.CharOffset
        // resolves to a node inside that paragraph (or to the paragraph itself).
        foreach (var p in spineText.Paragraphs)
        {
            var found = JsStyleFindNodeAtOffset(doc.Body!, p.CharOffset);
            found.Should().NotBeNull($"offset {p.CharOffset} should resolve to a DOM node");

            var ancestorIds = AncestorIds(found!.Element).ToList();
            ancestorIds.Should().Contain(p.ElementId,
                $"offset {p.CharOffset} should resolve inside element id='{p.ElementId}'");
        }
    }

    [Fact]
    public async Task FindNodeAtOffset_OffsetAtParagraphStart_LandsInsideThatParagraph()
    {
        const string xhtml = "<html><body><h1>AB</h1><p id=\"target\">CD</p></body></html>";
        // Text: "AB\nCD\n" — offsets: 0=A, 1=B, 2='\n', 3=C, 4=D, 5='\n'

        var doc = await ParseAsync(xhtml);
        var found = JsStyleFindNodeAtOffset(doc.Body!, 3);
        found.Should().NotBeNull();

        var ancestors = AncestorIds(found!.Element).ToList();
        ancestors.Should().Contain("target",
            "char offset 3 falls on 'C' which is inside the target paragraph");
    }

    [Fact]
    public async Task FindNodeAtOffset_OffsetWithinTextNode_ReportsCorrectLocalOffset()
    {
        const string xhtml = "<html><body><p>HelloWorld</p></body></html>";
        // Text: "HelloWorld\n"

        var doc = await ParseAsync(xhtml);
        var found = JsStyleFindNodeAtOffset(doc.Body!, 3);  // 'l' at index 3

        found.Should().NotBeNull();
        found!.LocalOffset.Should().Be(3);
    }

    // ──────── C# port of reader.js's text-accumulation walker ────────

    private static async Task<string> JsStyleTextAsync(string xhtml)
    {
        var doc = await ParseAsync(xhtml);
        var sb = new StringBuilder();
        if (doc.Body is null) return string.Empty;
        foreach (var c in doc.Body.ChildNodes) WalkText(c, sb);
        return sb.ToString();
    }

    private static void WalkText(INode node, StringBuilder sb)
    {
        if (node.NodeType == NodeType.Text)
        {
            sb.Append(node.NodeValue ?? string.Empty);
            return;
        }
        if (node.NodeType == NodeType.Comment) return;

        if (node is not IElement el)
        {
            foreach (var c in node.ChildNodes) WalkText(c, sb);
            return;
        }

        var tag = el.LocalName;
        if (string.Equals(tag, "script", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tag, "style", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var c in el.ChildNodes) WalkText(c, sb);
        if (BlockTags.Contains(tag)) sb.Append('\n');
    }

    // ──────── C# port of reader.js's findNodeAtOffset walker ────────

    private sealed record FoundNode(IElement Element, int LocalOffset);

    private static FoundNode? JsStyleFindNodeAtOffset(INode root, int target)
    {
        var state = new WalkState { Offset = 0, Found = null };
        WalkForOffset(root, target, state);
        return state.Found;
    }

    private sealed class WalkState
    {
        public int Offset;
        public FoundNode? Found;
    }

    private static void WalkForOffset(INode node, int target, WalkState state)
    {
        if (state.Found is not null) return;

        if (node.NodeType == NodeType.Text)
        {
            var len = (node.NodeValue ?? string.Empty).Length;
            if (state.Offset + len > target && node.ParentElement is not null)
                state.Found = new FoundNode(node.ParentElement, target - state.Offset);
            state.Offset += len;
            return;
        }
        if (node.NodeType == NodeType.Comment) return;

        if (node is not IElement el)
        {
            foreach (var c in node.ChildNodes)
            {
                WalkForOffset(c, target, state);
                if (state.Found is not null) return;
            }
            return;
        }

        var tag = el.LocalName;
        if (string.Equals(tag, "script", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tag, "style", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var c in el.ChildNodes)
        {
            WalkForOffset(c, target, state);
            if (state.Found is not null) return;
        }

        if (BlockTags.Contains(tag))
        {
            if (state.Offset == target)
                state.Found = new FoundNode(el, 0);
            state.Offset += 1;
        }
    }

    private static IEnumerable<string> AncestorIds(IElement element)
    {
        IElement? cur = element;
        while (cur is not null)
        {
            var id = cur.GetAttribute("id");
            if (!string.IsNullOrEmpty(id)) yield return id;
            cur = cur.ParentElement;
        }
    }

    private static async Task<SpineText> ExtractAsync(string xhtml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xhtml));
        return await SpineTextExtractor.ExtractAsync(stream);
    }

    private static async Task<IDocument> ParseAsync(string xhtml)
    {
        var ctx = BrowsingContext.New(Configuration.Default);
        var parser = ctx.GetService<IHtmlParser>()!;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xhtml));
        return await parser.ParseDocumentAsync(stream);
    }
}
