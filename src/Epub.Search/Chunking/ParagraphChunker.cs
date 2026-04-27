namespace Epub.Search.Chunking;

/// <summary>
/// Splits a <see cref="SpineText"/> into chunks at paragraph boundaries, capping each
/// chunk at <paramref name="maxTokens"/> as counted by the supplied <see cref="ITokenCounter"/>.
/// Paragraphs are never sliced; an over-cap paragraph is emitted as its own oversize chunk.
/// </summary>
/// <remarks>
/// Overlap is paragraph-granular rather than token-granular: each new chunk starts with
/// the last <paramref name="overlapParagraphs"/> paragraphs of the previous chunk.
/// This is a deviation from the original "32-token tail-overlap" sketch in search.md,
/// chosen because token-granular slicing would mean cutting paragraphs mid-sentence —
/// that cost outweighs the precision benefit at our scale.
/// </remarks>
public sealed class ParagraphChunker
{
    private readonly ITokenCounter _tokenCounter;
    private readonly int _maxTokens;
    private readonly int _overlapParagraphs;

    public ParagraphChunker(ITokenCounter tokenCounter, int maxTokens = 256, int overlapParagraphs = 1)
    {
        ArgumentNullException.ThrowIfNull(tokenCounter);
        if (maxTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxTokens));
        if (overlapParagraphs < 0) throw new ArgumentOutOfRangeException(nameof(overlapParagraphs));

        _tokenCounter = tokenCounter;
        _maxTokens = maxTokens;
        _overlapParagraphs = overlapParagraphs;
    }

    public IEnumerable<Chunk> Chunk(int spineIdx, SpineText spineText)
    {
        if (spineText.Paragraphs.Count == 0) yield break;

        var current = new List<(ParagraphSpan Span, int Tokens)>();
        int currentTokens = 0;

        foreach (var p in spineText.Paragraphs)
        {
            var pTokens = _tokenCounter.CountTokens(spineText.Text.Substring(p.CharOffset, p.CharLength));

            // Oversize paragraph: flush whatever we have, then emit it as a standalone chunk.
            if (pTokens > _maxTokens)
            {
                if (current.Count > 0)
                {
                    yield return BuildChunk(spineIdx, spineText, current);
                    current = new List<(ParagraphSpan, int)>();
                    currentTokens = 0;
                }
                yield return BuildChunk(spineIdx, spineText, new[] { (p, pTokens) });
                continue;
            }

            if (currentTokens + pTokens > _maxTokens && current.Count > 0)
            {
                yield return BuildChunk(spineIdx, spineText, current);

                // Carry the last N paragraphs forward as overlap into the new chunk.
                var overlap = current.Count >= _overlapParagraphs
                    ? current.GetRange(current.Count - _overlapParagraphs, _overlapParagraphs)
                    : new List<(ParagraphSpan, int)>(current);
                current = overlap;
                currentTokens = 0;
                foreach (var (_, t) in current) currentTokens += t;
            }

            current.Add((p, pTokens));
            currentTokens += pTokens;
        }

        if (current.Count > 0) yield return BuildChunk(spineIdx, spineText, current);
    }

    private static Chunk BuildChunk(
        int spineIdx, SpineText spineText, IReadOnlyList<(ParagraphSpan Span, int Tokens)> paragraphs)
    {
        var first = paragraphs[0].Span;
        var last = paragraphs[^1].Span;
        int offset = first.CharOffset;
        int length = (last.CharOffset + last.CharLength) - offset;
        return new Chunk(spineIdx, offset, length, spineText.Text.Substring(offset, length));
    }
}

public readonly record struct Chunk(int SpineIdx, int CharOffset, int CharLength, string Text);

public interface ITokenCounter
{
    int CountTokens(string text);
}
