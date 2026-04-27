using Epub.Core;
using Epub.Search.Chunking;
using Epub.Search.Embedding;

namespace Epub.Search;

public interface IIndexingService
{
    /// <summary>
    /// Index the given books in order. Already-indexed books are skipped silently.
    /// Errors on individual books are caught and surfaced via <see cref="IndexProgress.LastError"/>;
    /// indexing continues with the next book.
    /// </summary>
    Task BuildAllAsync(IReadOnlyList<string> bookPaths,
        IProgress<IndexProgress>? progress, CancellationToken ct);

    /// <summary>Index a single book. Errors propagate to the caller.</summary>
    Task IndexBookAsync(string bookPath,
        IProgress<IndexProgress>? progress, CancellationToken ct);

    bool IsRunning { get; }
}

public readonly record struct IndexProgress(
    int BookOrdinal,
    int BookCount,
    string CurrentBookTitle,
    int ChunksDone,
    int ChunksTotal,
    string? LastError = null);

public sealed class IndexingService : IIndexingService
{
    private readonly IEmbeddingStore _store;
    private readonly MiniLmEmbedder _embedder;
    private int _running;

    public IndexingService(IEmbeddingStore store, MiniLmEmbedder embedder)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embedder);
        _store = store;
        _embedder = embedder;
    }

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public async Task BuildAllAsync(IReadOnlyList<string> bookPaths,
        IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("Indexing already in progress.");
        try
        {
            await _embedder.EnsureReadyAsync(progress: null, ct).ConfigureAwait(false);

            for (int i = 0; i < bookPaths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await IndexOneAsync(bookPaths[i], i, bookPaths.Count, progress, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    progress?.Report(new IndexProgress(
                        BookOrdinal: i,
                        BookCount: bookPaths.Count,
                        CurrentBookTitle: Path.GetFileNameWithoutExtension(bookPaths[i]),
                        ChunksDone: 0,
                        ChunksTotal: 0,
                        LastError: ex.Message));
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    public async Task IndexBookAsync(string bookPath,
        IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("Indexing already in progress.");
        try
        {
            await _embedder.EnsureReadyAsync(progress: null, ct).ConfigureAwait(false);
            await IndexOneAsync(bookPath, 0, 1, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task IndexOneAsync(string bookPath, int ordinal, int total,
        IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        if (await _store.IsBookIndexedAsync(bookPath, ct).ConfigureAwait(false))
            return;

        using var reader = await EpubReader.OpenAsync(bookPath).ConfigureAwait(false);
        var title = string.IsNullOrWhiteSpace(reader.Book.Metadata.Title)
            ? Path.GetFileNameWithoutExtension(bookPath)
            : reader.Book.Metadata.Title;

        progress?.Report(new IndexProgress(ordinal, total, title, 0, 0));

        // Pass 1: extract + chunk every spine item so we know the total chunk count
        // (used for progress) and can batch-embed without re-walking the EPUB.
        var allChunks = await ExtractChunksAsync(reader, ct).ConfigureAwait(false);

        if (allChunks.Count == 0)
        {
            await using var emptySession = await _store.BeginIndexAsync(bookPath, ct).ConfigureAwait(false);
            await emptySession.CompleteAsync(ct).ConfigureAwait(false);
            progress?.Report(new IndexProgress(ordinal, total, title, 0, 0));
            return;
        }

        progress?.Report(new IndexProgress(ordinal, total, title, 0, allChunks.Count));

        // Pass 2: batch-embed and append.
        await using var session = await _store.BeginIndexAsync(bookPath, ct).ConfigureAwait(false);
        const int batchSize = MiniLmEmbedder.DefaultBatchSize;
        int chunksDone = 0;
        for (int i = 0; i < allChunks.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            int batchEnd = Math.Min(i + batchSize, allChunks.Count);
            var batchTexts = new string[batchEnd - i];
            for (int j = 0; j < batchTexts.Length; j++) batchTexts[j] = allChunks[i + j].Text;

            var embeddings = await _embedder.EmbedAsync(batchTexts, ct).ConfigureAwait(false);

            for (int j = 0; j < batchTexts.Length; j++)
            {
                var c = allChunks[i + j];
                await session.AppendChunkAsync(
                    c.SpineIdx, c.CharOffset, c.CharLength, c.Text,
                    embeddings[j], ct).ConfigureAwait(false);
                chunksDone++;
            }
            progress?.Report(new IndexProgress(ordinal, total, title, chunksDone, allChunks.Count));
        }

        await session.CompleteAsync(ct).ConfigureAwait(false);
    }

    private async Task<List<Chunking.Chunk>> ExtractChunksAsync(EpubReader reader, CancellationToken ct)
    {
        var chunker = new ParagraphChunker(_embedder);
        var allChunks = new List<Chunking.Chunk>();

        for (int spineIdx = 0; spineIdx < reader.Book.Spine.Count; spineIdx++)
        {
            ct.ThrowIfCancellationRequested();
            var item = reader.Book.Spine[spineIdx];

            if (!IsHtmlContent(item.ManifestItem.MediaType)) continue;

            var zipPath = reader.ResolveHref(item.ManifestItem.Href);
            if (!reader.ResourceExists(zipPath)) continue;

            SpineText spineText;
            using (var stream = reader.OpenResource(zipPath))
            {
                spineText = await SpineTextExtractor.ExtractAsync(stream, ct).ConfigureAwait(false);
            }

            foreach (var chunk in chunker.Chunk(spineIdx, spineText))
                allChunks.Add(chunk);
        }

        return allChunks;
    }

    private static bool IsHtmlContent(string mediaType)
        => mediaType.Contains("html", StringComparison.OrdinalIgnoreCase);
}
