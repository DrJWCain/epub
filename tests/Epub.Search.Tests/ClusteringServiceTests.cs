using Epub.Search.Clustering;
using FluentAssertions;

namespace Epub.Search.Tests;

public sealed class ClusteringServiceTests
{
    [Fact]
    public async Task RecomputeAsync_PartitionsChunksAndPersistsClusters()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        await PopulateThreeWellSeparatedGroups(store);

        var service = new ClusteringService(store);
        await service.RecomputeAsync(k: 3);

        var clusters = await store.GetClustersAsync();
        clusters.Should().HaveCount(3);
        clusters.Sum(c => c.ChunkCount).Should().Be(60, "every chunk lands in exactly one cluster");
        clusters.Should().AllSatisfy(c =>
            c.ChunkCount.Should().BeGreaterThan(0, "no empty clusters expected for well-separated data"));
    }

    [Fact]
    public async Task RecomputeAsync_PopulatesLabels_FromChunkText()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        // Three groups with topically distinct vocabulary so c-TF-IDF has signal.
        var rng = new Random(0);
        await using (var s = await store.BeginIndexAsync("c:/books/multi.epub"))
        {
            for (int i = 0; i < 20; i++)
                await s.AppendChunkAsync(0, i, 1,
                    "neural networks learn weights through gradient descent backpropagation",
                    Noisy(UnitVector(0, 8), rng));
            for (int i = 20; i < 40; i++)
                await s.AppendChunkAsync(0, i, 1,
                    "the railway system carries trains across the country on tracks",
                    Noisy(UnitVector(2, 8), rng));
            for (int i = 40; i < 60; i++)
                await s.AppendChunkAsync(0, i, 1,
                    "quantum mechanics describes electrons photons and superposition",
                    Noisy(UnitVector(4, 8), rng));
            await s.CompleteAsync();
        }

        var service = new ClusteringService(store);
        await service.RecomputeAsync(k: 3);

        var clusters = await store.GetClustersAsync();
        clusters.Should().AllSatisfy(c =>
            c.Label.Should().NotBeNullOrEmpty("RecomputeAsync also runs ClusterLabeler on the new clusters"));

        // The three labels should be disjoint — c-TF-IDF picks distinctive terms.
        var allLabels = clusters.Select(c => c.Label!).ToList();
        allLabels.Distinct().Should().HaveCount(3);
    }

    [Fact]
    public async Task RecomputeAsync_OverwritesPreviousClusters()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        await PopulateThreeWellSeparatedGroups(store);

        var service = new ClusteringService(store);
        await service.RecomputeAsync(k: 3);
        var first = await store.GetClustersAsync();

        await service.RecomputeAsync(k: 5);
        var second = await store.GetClustersAsync();

        second.Should().HaveCount(5, "re-running with a new K replaces the previous set");
        // Note: SQLite reuses rowids after DELETE on an INTEGER PRIMARY KEY column
        // (no AUTOINCREMENT), so cluster IDs may overlap between runs. The contract
        // is "the cluster set is replaced", not "ids never repeat".
    }

    [Fact]
    public async Task RecomputeAsync_EmptyCorpus_NoOps()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        var service = new ClusteringService(store);
        await service.RecomputeAsync(k: 3);

        (await store.GetClustersAsync()).Should().BeEmpty();
    }

    private static async Task PopulateThreeWellSeparatedGroups(EmbeddingStore store)
    {
        var rng = new Random(0);
        await using var session = await store.BeginIndexAsync("c:/books/synth.epub");
        for (int i = 0; i < 60; i++)
        {
            var axis = (i % 3) * 2;
            var vec = Noisy(UnitVector(axis, 8), rng);
            await session.AppendChunkAsync(0, i, 1, $"chunk{i}", vec);
        }
        await session.CompleteAsync();
    }

    private static float[] UnitVector(int axis, int dim)
    {
        var v = new float[dim];
        v[axis] = 1f;
        return v;
    }

    private static float[] Noisy(float[] v, Random rng)
    {
        var result = new float[v.Length];
        float ssq = 0;
        for (int i = 0; i < v.Length; i++)
        {
            result[i] = v[i] + (float)((rng.NextDouble() - 0.5) * 0.1);
            ssq += result[i] * result[i];
        }
        var norm = MathF.Sqrt(ssq);
        if (norm > 0) for (int i = 0; i < v.Length; i++) result[i] /= norm;
        return result;
    }

    private sealed class TempDb : IDisposable
    {
        public string Path { get; }
        private readonly string _dir;
        public TempDb()
        {
            _dir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "epub-search-clustering-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Path = System.IO.Path.Combine(_dir, "test.db");
        }
        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
