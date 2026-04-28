using FluentAssertions;

namespace Epub.Search.Tests;

public sealed class EmbeddingStoreTests
{
    [Fact]
    public async Task IsBookIndexed_ReturnsFalse_BeforeAnyIndexing()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        (await store.IsBookIndexedAsync("c:/books/foo.epub")).Should().BeFalse();
    }

    [Fact]
    public async Task IndexSession_Roundtrip_PersistsChunkAndEmbedding()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        await using (var session = await store.BeginIndexAsync("c:/books/foo.epub"))
        {
            await session.AppendChunkAsync(
                spineIdx: 0, charOffset: 0, charLength: 11,
                text: "hello world",
                embedding: UnitVector(0, 8));
            await session.CompleteAsync();
        }

        (await store.IsBookIndexedAsync("c:/books/foo.epub")).Should().BeTrue();

        var hits = await store.SearchAsync(UnitVector(0, 8), k: 1);
        hits.Should().ContainSingle();
        hits[0].SpineIdx.Should().Be(0);
        hits[0].CharOffset.Should().Be(0);
        hits[0].Snippet.Should().Be("hello world");
        hits[0].BookPath.Should().Be("c:/books/foo.epub");
        hits[0].Similarity.Should().BeApproximately(1.0f, 1e-5f);
    }

    [Fact]
    public async Task Search_RanksByCosineSimilarity()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        await using (var s = await store.BeginIndexAsync("c:/books/foo.epub"))
        {
            await s.AppendChunkAsync(0, 0, 5, "axis-a", UnitVector(0, 8));            // identical to query
            await s.AppendChunkAsync(0, 5, 5, "near-a", Normalize(new float[] { 0.9f, 0.1f, 0, 0, 0, 0, 0, 0 }));
            await s.AppendChunkAsync(0, 10, 5, "axis-b", UnitVector(1, 8));            // orthogonal
            await s.AppendChunkAsync(0, 15, 5, "anti-a", UnitVector(0, 8, sign: -1));  // opposite
            await s.CompleteAsync();
        }

        var hits = await store.SearchAsync(UnitVector(0, 8), k: 4);

        hits.Should().HaveCount(4);
        hits.Select(h => h.Snippet).Should().Equal("axis-a", "near-a", "axis-b", "anti-a");
        hits[0].Similarity.Should().BeApproximately(1.0f, 1e-5f);
        hits[1].Similarity.Should().BeGreaterThan(0.9f).And.BeLessThan(1.0f);
        hits[2].Similarity.Should().BeApproximately(0.0f, 1e-5f);
        hits[3].Similarity.Should().BeApproximately(-1.0f, 1e-5f);
    }

    [Fact]
    public async Task Search_TopK_WhenKExceedsCorpusSize_ReturnsAllChunks()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        await using (var s = await store.BeginIndexAsync("c:/books/foo.epub"))
        {
            await s.AppendChunkAsync(0, 0, 1, "a", UnitVector(0, 8));
            await s.AppendChunkAsync(0, 1, 1, "b", UnitVector(1, 8));
            await s.CompleteAsync();
        }

        var hits = await store.SearchAsync(UnitVector(0, 8), k: 100);
        hits.Should().HaveCount(2);
    }

    [Fact]
    public async Task DisposeWithoutComplete_RollsBack()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        await using (var session = await store.BeginIndexAsync("c:/books/foo.epub"))
        {
            await session.AppendChunkAsync(0, 0, 5, "first", UnitVector(0, 8));
            // Note: no CompleteAsync — falls out of using → rollback
        }

        (await store.IsBookIndexedAsync("c:/books/foo.epub")).Should().BeFalse();

        var hits = await store.SearchAsync(UnitVector(0, 8), k: 5);
        hits.Should().BeEmpty("the unfinished session must not leak chunks into the index");
    }

    [Fact]
    public async Task Reindex_WipesPriorPartialChunks_BeforeAppending()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        // First attempt: append two chunks then abandon (no Complete)
        await using (var s = await store.BeginIndexAsync("c:/books/foo.epub"))
        {
            await s.AppendChunkAsync(0, 0, 1, "OLD-A", UnitVector(0, 8));
            await s.AppendChunkAsync(0, 1, 1, "OLD-B", UnitVector(1, 8));
            // dispose without complete → rolled back
        }

        // Second attempt: complete with one chunk
        await using (var s = await store.BeginIndexAsync("c:/books/foo.epub"))
        {
            await s.AppendChunkAsync(0, 5, 1, "NEW", UnitVector(2, 8));
            await s.CompleteAsync();
        }

        var hits = await store.SearchAsync(UnitVector(2, 8), k: 10);
        hits.Should().ContainSingle()
            .Which.Snippet.Should().Be("NEW", "rollback already cleared OLD-A/B; only NEW is committed");
    }

    [Fact]
    public async Task Reindex_AfterCompletedRun_OverwritesEarlierChunks()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        await using (var s = await store.BeginIndexAsync("c:/books/foo.epub"))
        {
            await s.AppendChunkAsync(0, 0, 1, "FIRST-RUN", UnitVector(0, 8));
            await s.CompleteAsync();
        }

        await using (var s = await store.BeginIndexAsync("c:/books/foo.epub"))
        {
            await s.AppendChunkAsync(0, 0, 1, "SECOND-RUN", UnitVector(0, 8));
            await s.CompleteAsync();
        }

        var hits = await store.SearchAsync(UnitVector(0, 8), k: 10);
        hits.Should().ContainSingle().Which.Snippet.Should().Be("SECOND-RUN");
    }

    [Fact]
    public async Task GetIndexedBookPaths_ReturnsOnlyCompletedBooks_CaseInsensitive()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        // Two books complete, one abandoned mid-flight.
        await using (var s = await store.BeginIndexAsync("c:/Books/Alpha.epub"))
        {
            await s.AppendChunkAsync(0, 0, 1, "a", UnitVector(0, 8));
            await s.CompleteAsync();
        }
        await using (var s = await store.BeginIndexAsync("c:/Books/Beta.epub"))
        {
            await s.AppendChunkAsync(0, 0, 1, "b", UnitVector(1, 8));
            await s.CompleteAsync();
        }
        await using (var _ = await store.BeginIndexAsync("c:/Books/Gamma.epub"))
        {
            // No CompleteAsync — falls out as rolled back / not indexed
        }

        var indexed = await store.GetIndexedBookPathsAsync();

        indexed.Should().HaveCount(2);
        indexed.Should().Contain("c:/Books/Alpha.epub");
        indexed.Should().Contain("c:/Books/Beta.epub");
        indexed.Should().NotContain("c:/Books/Gamma.epub");
        indexed.Contains("C:/BOOKS/ALPHA.EPUB").Should().BeTrue(
            "the set is OrdinalIgnoreCase to match the schema's COLLATE NOCASE");
    }

    [Fact]
    public async Task GetMeta_ReturnsCounts_AndLastBuildTimestamp()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        var initial = await store.GetMetaAsync();
        initial.IndexedBookCount.Should().Be(0);
        initial.ChunkCount.Should().Be(0);
        initial.LastBuildAt.Should().BeNull();

        await using (var s = await store.BeginIndexAsync("c:/books/foo.epub"))
        {
            await s.AppendChunkAsync(0, 0, 1, "a", UnitVector(0, 8));
            await s.AppendChunkAsync(0, 1, 1, "b", UnitVector(1, 8));
            await s.CompleteAsync();
        }
        await using (var s = await store.BeginIndexAsync("c:/books/bar.epub"))
        {
            await s.AppendChunkAsync(0, 0, 1, "c", UnitVector(2, 8));
            await s.CompleteAsync();
        }

        var meta = await store.GetMetaAsync();
        meta.IndexedBookCount.Should().Be(2);
        meta.ChunkCount.Should().Be(3);
        meta.LastBuildAt.Should().NotBeNull();
        meta.LastBuildAt!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Search_ReturnsLongTextSnippet_TruncatedAtSentenceWhenPossible()
    {
        using var temp = new TempDb();
        var store = new EmbeddingStore(temp.Path);

        // Need a sentence terminator past the midpoint of the 240-char budget
        // (otherwise the snippet logic falls through to ellipsis truncation).
        var longText =
            string.Concat(Enumerable.Repeat("word ", 30)) +  // ~150 chars
            "is the end of the first sentence here. " +     // period lands well past 120
            new string('x', 200);

        await using (var s = await store.BeginIndexAsync("c:/books/foo.epub"))
        {
            await s.AppendChunkAsync(0, 0, longText.Length, longText, UnitVector(0, 8));
            await s.CompleteAsync();
        }

        var hits = await store.SearchAsync(UnitVector(0, 8), k: 1);
        hits[0].Snippet.Should().EndWith(".", "snippet should prefer ending on a sentence terminator");
        hits[0].Snippet.Length.Should().BeLessThanOrEqualTo(240);
    }

    private static ReadOnlyMemory<float> UnitVector(int axis, int dim, int sign = 1)
    {
        var v = new float[dim];
        v[axis] = sign;
        return v;
    }

    private static ReadOnlyMemory<float> Normalize(float[] v)
    {
        float sumSq = 0;
        foreach (var x in v) sumSq += x * x;
        var norm = MathF.Sqrt(sumSq);
        if (norm == 0) return v;
        var result = new float[v.Length];
        for (int i = 0; i < v.Length; i++) result[i] = v[i] / norm;
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
                "epub-search-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Path = System.IO.Path.Combine(_dir, "test.db");
        }

        public void Dispose()
        {
            // SQLite + WAL leaves -wal and -shm sidecar files; the directory cleanup gets them.
            try { Directory.Delete(_dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
