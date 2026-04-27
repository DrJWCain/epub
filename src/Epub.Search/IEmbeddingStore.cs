namespace Epub.Search;

public interface IEmbeddingStore
{
    /// <summary>
    /// Begin indexing a book. Any prior partial chunks for the same book_path are wiped.
    /// Caller MUST either call <see cref="IIndexSession.CompleteAsync"/> on the returned
    /// session or dispose without completing (the transaction will roll back).
    /// </summary>
    Task<IIndexSession> BeginIndexAsync(string bookPath, CancellationToken ct = default);

    Task<bool> IsBookIndexedAsync(string bookPath, CancellationToken ct = default);

    /// <summary>
    /// Top-K nearest chunks by cosine similarity to <paramref name="queryEmbedding"/>.
    /// Results are ordered by descending similarity (most similar first).
    /// </summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding, int k, CancellationToken ct = default);

    Task<IndexMeta> GetMetaAsync(CancellationToken ct = default);
}

public interface IIndexSession : IAsyncDisposable
{
    long BookId { get; }

    Task AppendChunkAsync(int spineIdx, int charOffset, int charLength, string text,
        ReadOnlyMemory<float> embedding, CancellationToken ct = default);

    /// <summary>Commit the indexing transaction and mark the book as fully indexed.</summary>
    Task CompleteAsync(CancellationToken ct = default);
}

public sealed record SearchHit(
    long ChunkId,
    long BookId,
    string BookPath,
    int SpineIdx,
    int CharOffset,
    int CharLength,
    string Snippet,
    float Similarity);

public sealed record IndexMeta(
    string? Model,
    int IndexedBookCount,
    long ChunkCount,
    DateTimeOffset? LastBuildAt);
