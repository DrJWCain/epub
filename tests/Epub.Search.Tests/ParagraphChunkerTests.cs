using System.Text;
using Epub.Search;
using Epub.Search.Chunking;
using FluentAssertions;

namespace Epub.Search.Tests;

public sealed class ParagraphChunkerTests
{
    [Fact]
    public void Chunk_EmptySpineText_YieldsNoChunks()
    {
        var chunker = new ParagraphChunker(new WhitespaceTokenCounter(), maxTokens: 100);
        var chunks = chunker.Chunk(0, new SpineText("", Array.Empty<ParagraphSpan>())).ToList();
        chunks.Should().BeEmpty();
    }

    [Fact]
    public void Chunk_SingleParagraphUnderCap_YieldsOneChunk()
    {
        var text = BuildText(("alpha beta gamma", "p1"));
        var chunker = new ParagraphChunker(new WhitespaceTokenCounter(), maxTokens: 100);

        var chunks = chunker.Chunk(spineIdx: 7, text).ToList();

        chunks.Should().ContainSingle();
        chunks[0].SpineIdx.Should().Be(7);
        chunks[0].CharOffset.Should().Be(0);
        chunks[0].Text.Should().Be("alpha beta gamma\n");
    }

    [Fact]
    public void Chunk_MultipleParagraphsAllFit_YieldOneChunkSpanningAll()
    {
        var text = BuildText(("one two", "p1"), ("three four", "p2"), ("five", "p3"));
        var chunker = new ParagraphChunker(new WhitespaceTokenCounter(), maxTokens: 100);

        var chunks = chunker.Chunk(0, text).ToList();

        chunks.Should().ContainSingle();
        chunks[0].CharOffset.Should().Be(0);
        chunks[0].Text.Should().Be("one two\nthree four\nfive\n");
    }

    [Fact]
    public void Chunk_OverflowsCap_SplitsAtParagraphBoundary_WithSinglePragraphOverlap()
    {
        // 3 paragraphs of 3, 3, and 3 tokens each. Cap = 6.
        // p1+p2 fits (6); adding p3 would be 9 — flush.
        // Overlap = 1: chunk2 starts with p2, then p3 → 6 tokens.
        var text = BuildText(("a a a", "p1"), ("b b b", "p2"), ("c c c", "p3"));
        var chunker = new ParagraphChunker(new WhitespaceTokenCounter(), maxTokens: 6, overlapParagraphs: 1);

        var chunks = chunker.Chunk(0, text).ToList();

        chunks.Should().HaveCount(2);
        chunks[0].Text.Should().Be("a a a\nb b b\n");
        chunks[1].Text.Should().Be("b b b\nc c c\n");
        chunks[1].CharOffset.Should().Be(text.Paragraphs[1].CharOffset);
    }

    [Fact]
    public void Chunk_OverlapZero_NoSharedParagraphs()
    {
        var text = BuildText(("a a a", "p1"), ("b b b", "p2"), ("c c c", "p3"), ("d d d", "p4"));
        var chunker = new ParagraphChunker(new WhitespaceTokenCounter(), maxTokens: 6, overlapParagraphs: 0);

        var chunks = chunker.Chunk(0, text).ToList();

        chunks.Should().HaveCount(2);
        chunks[0].Text.Should().Be("a a a\nb b b\n");
        chunks[1].Text.Should().Be("c c c\nd d d\n");
    }

    [Fact]
    public void Chunk_OversizeParagraph_FlushesCurrentChunk_ThenEmitsAlone()
    {
        var huge = string.Join(' ', Enumerable.Repeat("word", 50));  // 50 tokens
        var text = BuildText(("small one", "p1"), (huge, "p2"), ("after", "p3"));
        var chunker = new ParagraphChunker(new WhitespaceTokenCounter(), maxTokens: 10, overlapParagraphs: 1);

        var chunks = chunker.Chunk(0, text).ToList();

        chunks.Should().HaveCount(3);
        chunks[0].Text.Should().Be("small one\n");
        chunks[1].Text.Should().Be(huge + "\n");
        chunks[1].CharLength.Should().Be(huge.Length + 1, "the oversize paragraph is emitted whole");
        chunks[2].Text.Should().Be("after\n");
    }

    [Fact]
    public void Chunk_FirstParagraphAloneIsOversize_StillEmitsAsStandaloneChunk()
    {
        var huge = string.Join(' ', Enumerable.Repeat("word", 50));
        var text = BuildText((huge, "p1"));
        var chunker = new ParagraphChunker(new WhitespaceTokenCounter(), maxTokens: 10);

        var chunks = chunker.Chunk(0, text).ToList();

        chunks.Should().ContainSingle();
        chunks[0].Text.Should().Be(huge + "\n");
    }

    [Fact]
    public void Chunk_AllChunkTextSlicesFromSpineText()
    {
        // Property: every chunk's Text must equal SpineText.Text.AsSpan(offset, length).
        var text = BuildText(
            ("alpha bravo charlie", "p1"),
            ("delta echo", "p2"),
            ("foxtrot golf hotel india", "p3"),
            ("juliet", "p4"),
            ("kilo lima mike november", "p5"));
        var chunker = new ParagraphChunker(new WhitespaceTokenCounter(), maxTokens: 7, overlapParagraphs: 1);

        var chunks = chunker.Chunk(0, text).ToList();

        foreach (var c in chunks)
        {
            text.Text.Substring(c.CharOffset, c.CharLength).Should().Be(c.Text);
            c.SpineIdx.Should().Be(0);
        }
    }

    [Fact]
    public void Constructor_RejectsBadArguments()
    {
        var counter = new WhitespaceTokenCounter();
        FluentActions.Invoking(() => new ParagraphChunker(counter, maxTokens: 0))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new ParagraphChunker(counter, overlapParagraphs: -1))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new ParagraphChunker(null!))
            .Should().Throw<ArgumentNullException>();
    }

    private static SpineText BuildText(params (string text, string id)[] paragraphs)
    {
        var sb = new StringBuilder();
        var spans = new List<ParagraphSpan>();
        foreach (var (text, id) in paragraphs)
        {
            int offset = sb.Length;
            sb.Append(text);
            sb.Append('\n');
            spans.Add(new ParagraphSpan(offset, sb.Length - offset, id));
        }
        return new SpineText(sb.ToString(), spans);
    }

    private sealed class WhitespaceTokenCounter : ITokenCounter
    {
        public int CountTokens(string text)
            => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
