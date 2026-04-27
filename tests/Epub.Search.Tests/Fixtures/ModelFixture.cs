using Epub.Search.Embedding;

namespace Epub.Search.Tests.Fixtures;

/// <summary>
/// Shared fixture for integration tests that need a real loaded MiniLM ONNX
/// session and BertTokenizer. Downloads once into a stable cache directory
/// (so re-running the suite is instant after the first time) and reuses a
/// single InferenceSession across every test in the collection — without this,
/// each test paid ~30 s of session warm-up and tokenizer init.
///
/// If the model isn't cached AND the EPUB_SEARCH_DOWNLOAD_MODEL env var isn't
/// set to "1", the fixture leaves <see cref="Embedder"/> null. Integration
/// tests should treat null as "skip silently" so the suite stays runnable on
/// machines / CI where downloading 90 MB is unwanted.
/// </summary>
public sealed class ModelFixture : IAsyncLifetime
{
    public static readonly string SharedCacheDir =
        Path.Combine(Path.GetTempPath(), "epub-search-test-models");

    public MiniLmModelDownloader Downloader { get; }
    public MiniLmEmbedder? Embedder { get; private set; }

    public bool IsAvailable => Embedder is not null;

    public ModelFixture()
    {
        Downloader = new MiniLmModelDownloader(SharedCacheDir);
    }

    public async Task InitializeAsync()
    {
        if (!Downloader.IsDownloaded)
        {
            if (Environment.GetEnvironmentVariable("EPUB_SEARCH_DOWNLOAD_MODEL") != "1")
                return;  // silent skip for the whole collection
            await Downloader.EnsureDownloadedAsync(progress: null, ct: default);
        }

        var embedder = new MiniLmEmbedder(Downloader);
        await embedder.EnsureReadyAsync();
        Embedder = embedder;
    }

    public Task DisposeAsync()
    {
        Embedder?.Dispose();
        return Task.CompletedTask;
    }
}

[CollectionDefinition(Name)]
public sealed class ModelCollection : ICollectionFixture<ModelFixture>
{
    public const string Name = "MiniLM model";
}
