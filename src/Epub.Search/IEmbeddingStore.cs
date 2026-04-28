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
    /// Returns the absolute paths of every book whose indexing has completed.
    /// One round-trip — preferred over N calls to <see cref="IsBookIndexedAsync"/>
    /// when the caller wants to filter a list.
    /// </summary>
    Task<IReadOnlySet<string>> GetIndexedBookPathsAsync(CancellationToken ct = default);

    /// <summary>
    /// Top-K nearest chunks by cosine similarity to <paramref name="queryEmbedding"/>.
    /// Results are ordered by descending similarity (most similar first).
    /// </summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding, int k, CancellationToken ct = default);

    Task<IndexMeta> GetMetaAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams every (chunk_id, embedding) pair into a callback. Lets the
    /// clustering pass walk the corpus without materialising 250k × 384-dim
    /// floats into RAM all at once.
    /// </summary>
    Task EnumerateEmbeddingsAsync(
        Func<long, ReadOnlyMemory<float>, CancellationToken, Task> onPair,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically replaces all clusters and chunk-cluster assignments with the
    /// supplied set. Both tables are wiped before the new rows are written.
    /// </summary>
    Task RewriteClustersAsync(
        IReadOnlyList<float[]> centroids,
        IReadOnlyList<(long ChunkId, int ClusterIdx)> assignments,
        CancellationToken ct = default);

    Task<IReadOnlyList<ClusterRow>> GetClustersAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams every (cluster_id, chunk_text) pair so the labeler can walk the
    /// post-clustering corpus without materialising it.
    /// </summary>
    Task EnumerateClusterChunkTextsAsync(
        Func<long, string, CancellationToken, Task> onPair,
        CancellationToken ct = default);

    /// <summary>Batch-update cluster labels in a single transaction.</summary>
    Task UpdateClusterLabelsAsync(
        IReadOnlyDictionary<long, string> labels,
        CancellationToken ct = default);
}

public sealed record ClusterRow(
    long Id,
    string? Label,
    int ChunkCount,
    DateTimeOffset BuiltAt);

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
