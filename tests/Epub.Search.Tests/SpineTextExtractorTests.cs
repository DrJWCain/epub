using System.Text;
using Epub.Search;
using FluentAssertions;

namespace Epub.Search.Tests;

public sealed class SpineTextExtractorTests
{
    /// <summary>
    /// Regression fixture for the offset contract. Hand-computed text and spans here
    /// MUST match what the reader.js DOM walker (P6) would produce on the same XHTML;
    /// any drift breaks search-result navigation.
    /// </summary>
    [Fact]
    public async Task Extract_HandComputedFixture_ProducesExactTextAndSpans()
    {
        // No whitespace between block tags so AngleSharp doesn't insert text nodes between them.
        const string xhtml =
            "<html><body>" +
            "<h1 id=\"ch1-title\">Hello</h1>" +
            "<p>First paragraph.</p>" +
            "<p>Second paragraph with <em>inline</em> markup.</p>" +
            "<script>var x = 1;</script>" +
            "<pre id=\"code\">code block</pre>" +
            "</body></html>";

        var spineText = await ExtractAsync(xhtml);

        spineText.Text.Should().Be(
            "Hello\nFirst paragraph.\nSecond paragraph with inline markup.\ncode block\n");

        spineText.Paragraphs.Should().Equal(
            new ParagraphSpan(0, 6, "ch1-title"),  // "Hello\n"
            new ParagraphSpan(6, 17, ""),           // "First paragraph.\n"
            new ParagraphSpan(23, 37, ""),          // "Second paragraph with inline markup.\n"
            new ParagraphSpan(60, 11, "code"));     // "code block\n"
    }

    [Fact]
    public async Task Extract_PreservesWhitespaceInTextNodes_Verbatim()
    {
        const string xhtml = "<html><body><p>multiple   spaces  and\ttabs</p></body></html>";
        var spineText = await ExtractAsync(xhtml);
        spineText.Text.Should().Be("multiple   spaces  and\ttabs\n");
    }

    [Fact]
    public async Task Extract_SkipsScriptAndStyleSubtrees()
    {
        const string xhtml =
            "<html><body>" +
            "<p>before</p>" +
            "<script>console.log('hidden')</script>" +
            "<style>p { color: red }</style>" +
            "<p>after</p>" +
            "</body></html>";

        var spineText = await ExtractAsync(xhtml);
        spineText.Text.Should().Be("before\nafter\n");
    }

    [Fact]
    public async Task Extract_SkipsHtmlComments()
    {
        const string xhtml =
            "<html><body><p>visible</p><!-- a comment --><p>also visible</p></body></html>";

        var spineText = await ExtractAsync(xhtml);
        spineText.Text.Should().Be("visible\nalso visible\n");
    }

    [Fact]
    public async Task Extract_NestedParagraphsInBlockquote_RecordsInnerOnly()
    {
        const string xhtml = "<html><body><blockquote><p>Inside</p></blockquote></body></html>";

        var spineText = await ExtractAsync(xhtml);

        // p close emits '\n', then blockquote close emits another '\n'.
        spineText.Text.Should().Be("Inside\n\n");
        spineText.Paragraphs.Should().ContainSingle()
            .Which.Should().Be(new ParagraphSpan(0, 7, ""));
    }

    [Fact]
    public async Task Extract_DivWithoutInnerParagraphs_RecordsTheDivItself()
    {
        const string xhtml = "<html><body><div id=\"loose\">just text in a div</div></body></html>";

        var spineText = await ExtractAsync(xhtml);

        spineText.Text.Should().Be("just text in a div\n");
        spineText.Paragraphs.Should().ContainSingle()
            .Which.Should().Be(new ParagraphSpan(0, 19, "loose"));
    }

    [Fact]
    public async Task Extract_EmptyAndWhitespaceOnlyParagraphs_AreNotRecorded()
    {
        const string xhtml =
            "<html><body><p></p><p>real content</p><p>   </p></body></html>";

        var spineText = await ExtractAsync(xhtml);

        spineText.Paragraphs.Should().ContainSingle();
        var only = spineText.Paragraphs[0];
        spineText.Text.AsSpan(only.CharOffset, only.CharLength).TrimEnd().ToString()
            .Should().Be("real content");
    }

    [Fact]
    public async Task Extract_DecodesNamedEntities()
    {
        const string xhtml = "<html><body><p>caf&eacute;&nbsp;noir &amp; co.</p></body></html>";

        var spineText = await ExtractAsync(xhtml);
        spineText.Text.Should().Be("café noir & co.\n");
    }

    [Fact]
    public async Task Extract_BrInlineLineBreak_EmitsNewline()
    {
        const string xhtml = "<html><body><p>line one<br/>line two</p></body></html>";

        var spineText = await ExtractAsync(xhtml);
        spineText.Text.Should().Be("line one\nline two\n");
    }

    [Fact]
    public async Task Extract_NoBody_ReturnsEmpty()
    {
        const string xhtml = "<html><head><title>x</title></head></html>";

        var spineText = await ExtractAsync(xhtml);
        spineText.Text.Should().BeEmpty();
        spineText.Paragraphs.Should().BeEmpty();
    }

    [Fact]
    public async Task Extract_ParagraphBoundariesSliceCleanlyFromText()
    {
        // Property: every recorded ParagraphSpan must be slicable from SpineText.Text.
        const string xhtml =
            "<html><body>" +
            "<h2>Chapter 1</h2>" +
            "<p>Para A.</p>" +
            "<p>Para B with <strong>bold</strong> word.</p>" +
            "<ul><li>Item 1</li><li>Item 2</li></ul>" +
            "</body></html>";

        var spineText = await ExtractAsync(xhtml);

        foreach (var p in spineText.Paragraphs)
        {
            var slice = spineText.Text.AsSpan(p.CharOffset, p.CharLength);
            slice.Length.Should().Be(p.CharLength);
            slice.IsEmpty.Should().BeFalse();
        }
    }

    private static async Task<SpineText> ExtractAsync(string xhtml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xhtml));
        return await SpineTextExtractor.ExtractAsync(stream);
    }
}
