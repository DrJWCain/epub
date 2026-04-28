namespace Epub.Search.Clustering;

public interface IClusteringService
{
    /// <summary>
    /// Loads every chunk embedding, clusters them with K-means, and atomically
    /// rewrites the clusters + chunk_clusters tables. Idempotent: re-running
    /// replaces the previous theme set. K is auto-selected from chunk count
    /// unless overridden.
    /// </summary>
    Task RecomputeAsync(int? k = null, IProgress<ClusteringProgress>? progress = null,
        CancellationToken ct = default);

    bool IsRunning { get; }
}

public readonly record struct ClusteringProgress(string Phase, int Current, int Total);

public sealed class ClusteringService : IClusteringService
{
    private readonly IEmbeddingStore _store;
    private int _running;

    public ClusteringService(IEmbeddingStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public async Task RecomputeAsync(int? k = null, IProgress<ClusteringProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("Clustering already in progress.");
        try
        {
            progress?.Report(new ClusteringProgress("Loading embeddings", 0, 0));
            var chunkIds = new List<long>();
            var vectors = new List<float[]>();
            await _store.EnumerateEmbeddingsAsync((id, vec, _) =>
            {
                chunkIds.Add(id);
                vectors.Add(vec.ToArray());
                if (vectors.Count % 5000 == 0)
                    progress?.Report(new ClusteringProgress("Loading embeddings", vectors.Count, 0));
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);

            if (vectors.Count == 0) return;

            var actualK = k ?? AutoK(vectors.Count);
            var clusterer = new KMeansClusterer(actualK, maxIterations: 30, seed: 42);

            var kmProgress = new Progress<KMeansProgress>(p =>
                progress?.Report(new ClusteringProgress(
                    Phase: $"Clustering ({p.Reassignments} reassignments)",
                    Current: p.Iteration,
                    Total: p.MaxIterations)));

            var result = await Task.Run(
                () => clusterer.Cluster(vectors, kmProgress, ct), ct).ConfigureAwait(false);

            progress?.Report(new ClusteringProgress("Persisting clusters", 0, result.Centroids.Length));
            var assignments = new (long ChunkId, int ClusterIdx)[chunkIds.Count];
            for (int i = 0; i < chunkIds.Count; i++)
                assignments[i] = (chunkIds[i], result.Assignments[i]);

            await _store.RewriteClustersAsync(result.Centroids, assignments, ct).ConfigureAwait(false);

            progress?.Report(new ClusteringProgress("Computing labels", 0, result.Centroids.Length));
            var perCluster = new List<(long ClusterId, string Text)>(chunkIds.Count);
            await _store.EnumerateClusterChunkTextsAsync((cid, text, _) =>
            {
                perCluster.Add((cid, text));
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);

            var labeler = new ClusterLabeler(topN: 4);
            var labels = await Task.Run(
                () => labeler.ComputeLabels(perCluster), ct).ConfigureAwait(false);

            progress?.Report(new ClusteringProgress("Persisting labels", 0, labels.Count));
            await _store.UpdateClusterLabelsAsync(labels, ct).ConfigureAwait(false);

            progress?.Report(new ClusteringProgress("Done", result.Centroids.Length, result.Centroids.Length));
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// Default K = clamp(round(N / 500), 10, 50). Yields ~100–500 chunks per
    /// cluster on average, which is small enough to browse and large enough
    /// that c-TF-IDF labels (S3.1) have signal.
    /// </summary>
    private static int AutoK(int chunkCount)
        => Math.Max(10, Math.Min(50, (int)Math.Round(chunkCount / 500.0)));
}
