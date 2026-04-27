using Epub.Search.Embedding;
using Epub.Search.Tests.Fixtures;
using FluentAssertions;

namespace Epub.Search.Tests;

/// <summary>
/// MiniLmEmbedder argument-validation and lifecycle tests run unconditionally.
/// Inference tests share a single warmed embedder via <see cref="ModelFixture"/>;
/// they auto-skip if the model isn't cached. Set EPUB_SEARCH_DOWNLOAD_MODEL=1 to
/// trigger a one-time download into the shared test cache.
/// </summary>
[Collection(ModelCollection.Name)]
public sealed class MiniLmEmbedderTests
{
    private readonly ModelFixture _model;

    public MiniLmEmbedderTests(ModelFixture model) => _model = model;

    [Fact]
    public void Constructor_RejectsNullDownloader()
    {
        FluentActions.Invoking(() => new MiniLmEmbedder(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task EmbedAsync_BeforeEnsureReady_Throws()
    {
        using var temp = new TempDir();
        var downloader = new MiniLmModelDownloader(temp.Path);
        using var embedder = new MiniLmEmbedder(downloader);

        var act = async () => await embedder.EmbedAsync(new[] { "hello" });
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void CountTokens_BeforeEnsureReady_Throws()
    {
        using var temp = new TempDir();
        var downloader = new MiniLmModelDownloader(temp.Path);
        using var embedder = new MiniLmEmbedder(downloader);

        FluentActions.Invoking(() => embedder.CountTokens("hello"))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Dimension_Returns384_Always()
    {
        using var temp = new TempDir();
        var downloader = new MiniLmModelDownloader(temp.Path);
        using var embedder = new MiniLmEmbedder(downloader);

        embedder.Dimension.Should().Be(MiniLmEmbedder.EmbeddingDimension).And.Be(384);
    }

    // ──────── integration tests below; auto-skip if shared model isn't cached ────────

    [Fact]
    public async Task Embed_ProducesUnitNormalizedVectorsOfDimension384()
    {
        if (!_model.IsAvailable) return;

        var vectors = await _model.Embedder!.EmbedAsync(new[] { "A short sentence about cats." });

        vectors.Should().ContainSingle();
        vectors[0].Length.Should().Be(384);

        var ssq = 0.0;
        foreach (var x in vectors[0]) ssq += x * x;
        Math.Sqrt(ssq).Should().BeApproximately(1.0, 1e-3,
            "L2 normalization should produce unit-length vectors");
    }

    [Fact]
    public async Task Embed_SimilarSentences_HaveHigherCosineThanUnrelated()
    {
        if (!_model.IsAvailable) return;

        var vectors = await _model.Embedder!.EmbedAsync(new[]
        {
            "The cat sat on the mat.",
            "A feline rested on the rug.",
            "Quantum mechanics describes subatomic particles.",
        });

        var simNear = Cosine(vectors[0], vectors[1]);
        var simFar = Cosine(vectors[0], vectors[2]);

        simNear.Should().BeGreaterThan(simFar,
            "synonymous sentences should be closer in embedding space than unrelated ones");
    }

    [Fact]
    public void CountTokens_OnSimpleSentence_ReturnsReasonableCount()
    {
        if (!_model.IsAvailable) return;

        var n = _model.Embedder!.CountTokens("The quick brown fox jumps over the lazy dog.");
        n.Should().BeInRange(8, 20, "BERT WordPiece for a 9-word English sentence falls in this range");
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0;
        for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;  // vectors are unit-normalized so dot == cosine
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "epub-search-embedder-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
