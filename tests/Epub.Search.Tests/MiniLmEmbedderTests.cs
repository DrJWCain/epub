using Epub.Search.Embedding;
using FluentAssertions;

namespace Epub.Search.Tests;

/// <summary>
/// MiniLmEmbedder argument-validation and lifecycle tests run unconditionally.
/// Inference tests are gated on the MiniLM model being already downloaded into
/// <see cref="SharedTestCacheDir"/>; the first time you let the app build its
/// index that download will populate the cache, and after that these tests
/// activate automatically. To trigger the download manually, set the env var
/// EPUB_SEARCH_DOWNLOAD_MODEL=1 before running the test suite.
/// </summary>
public sealed class MiniLmEmbedderTests
{
    private static readonly string SharedTestCacheDir = Path.Combine(
        Path.GetTempPath(), "epub-search-test-models");

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

    // ──────── integration tests below; auto-skip if model isn't cached ────────

    [Fact]
    public async Task Embed_ProducesUnitNormalizedVectorsOfDimension384()
    {
        if (await EnsureModelOrSkip() is not { } embedder) return;
        using var _ = embedder;

        var vectors = await embedder.EmbedAsync(new[] { "A short sentence about cats." });

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
        if (await EnsureModelOrSkip() is not { } embedder) return;
        using var _ = embedder;

        var vectors = await embedder.EmbedAsync(new[]
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
    public async Task CountTokens_OnSimpleSentence_ReturnsReasonableCount()
    {
        if (await EnsureModelOrSkip() is not { } embedder) return;
        using var _ = embedder;

        var n = embedder.CountTokens("The quick brown fox jumps over the lazy dog.");
        n.Should().BeInRange(8, 20, "BERT WordPiece for a 9-word English sentence falls in this range");
    }

    private static async Task<MiniLmEmbedder?> EnsureModelOrSkip()
    {
        var downloader = new MiniLmModelDownloader(SharedTestCacheDir);
        if (!downloader.IsDownloaded)
        {
            if (Environment.GetEnvironmentVariable("EPUB_SEARCH_DOWNLOAD_MODEL") != "1")
                return null;  // silent skip
            await downloader.EnsureDownloadedAsync(progress: null, ct: default);
        }
        var embedder = new MiniLmEmbedder(downloader);
        await embedder.EnsureReadyAsync();
        return embedder;
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
