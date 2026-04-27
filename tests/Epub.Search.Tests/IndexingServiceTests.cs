using System.IO.Compression;
using System.Text;
using Epub.Search.Embedding;
using Epub.Search.Tests.Fixtures;
using FluentAssertions;

namespace Epub.Search.Tests;

/// <summary>
/// End-to-end tests for IndexingService. Share a single warmed embedder via
/// <see cref="ModelFixture"/> so the suite runs in seconds rather than minutes.
/// Auto-skip if the shared model isn't cached.
/// </summary>
[Collection(ModelCollection.Name)]
public sealed class IndexingServiceTests
{
    private readonly ModelFixture _model;

    public IndexingServiceTests(ModelFixture model) => _model = model;

    [Fact]
    public async Task IndexBook_EndToEnd_PersistsChunksAndEnablesSemanticSearch()
    {
        await using var bench = TestBench.Create(_model);
        if (bench is null) return;

        var bookPath = bench.WriteEpub(SyntheticEpub3Spines());

        await bench.IndexingService.IndexBookAsync(bookPath, progress: null, ct: default);

        (await bench.Store.IsBookIndexedAsync(bookPath)).Should().BeTrue();

        var meta = await bench.Store.GetMetaAsync();
        meta.IndexedBookCount.Should().Be(1);
        meta.ChunkCount.Should().BeGreaterThan(0);

        // Query each spine's distinctive concept and assert the top hit comes from
        // the matching spine item.
        var catHits = await SearchByText(bench, "feline animal");
        catHits[0].SpineIdx.Should().Be(0, "spine 0 is the chapter about cats");

        var trainHits = await SearchByText(bench, "locomotive railway");
        trainHits[0].SpineIdx.Should().Be(1, "spine 1 is the chapter about trains");

        var quantumHits = await SearchByText(bench, "quantum subatomic");
        quantumHits[0].SpineIdx.Should().Be(2, "spine 2 is the chapter about physics");
    }

    [Fact]
    public async Task IndexBook_AlreadyIndexed_NoOpsAndDoesNotDuplicate()
    {
        await using var bench = TestBench.Create(_model);
        if (bench is null) return;

        var bookPath = bench.WriteEpub(SyntheticEpub3Spines());

        await bench.IndexingService.IndexBookAsync(bookPath, progress: null, ct: default);
        var firstMeta = await bench.Store.GetMetaAsync();

        await bench.IndexingService.IndexBookAsync(bookPath, progress: null, ct: default);
        var secondMeta = await bench.Store.GetMetaAsync();

        secondMeta.ChunkCount.Should().Be(firstMeta.ChunkCount,
            "the second IndexBookAsync should observe IsBookIndexed=true and bail early");
    }

    [Fact]
    public async Task BuildAll_ReportsProgressForEachBook_WithChunkCounts()
    {
        await using var bench = TestBench.Create(_model);
        if (bench is null) return;

        var path1 = bench.WriteEpub(SyntheticEpub3Spines(), "a.epub");
        var path2 = bench.WriteEpub(SyntheticEpub3Spines(), "b.epub");

        var captures = new CapturingProgress<IndexProgress>();
        await bench.IndexingService.BuildAllAsync(new[] { path1, path2 }, captures, ct: default);

        captures.Reports.Should().NotBeEmpty();
        captures.Reports.Should().Contain(r => r.BookOrdinal == 0 && r.ChunksDone > 0);
        captures.Reports.Should().Contain(r => r.BookOrdinal == 1 && r.ChunksDone > 0);
        captures.Reports.Last(r => r.BookOrdinal == 1).ChunksDone
            .Should().Be(captures.Reports.Last(r => r.BookOrdinal == 1).ChunksTotal);
    }

    [Fact]
    public async Task BuildAll_ContinuesPastBookErrors_AndReportsLastError()
    {
        await using var bench = TestBench.Create(_model);
        if (bench is null) return;

        var goodPath = bench.WriteEpub(SyntheticEpub3Spines(), "good.epub");
        var badPath = Path.Combine(bench.TempDir, "missing.epub");  // does not exist

        var captures = new CapturingProgress<IndexProgress>();
        await bench.IndexingService.BuildAllAsync(new[] { badPath, goodPath }, captures, ct: default);

        captures.Reports.Should().Contain(r => r.LastError != null,
            "the missing file should surface as a per-book error");
        (await bench.Store.IsBookIndexedAsync(goodPath)).Should().BeTrue(
            "the good book should still complete after the bad one fails");
    }

    [Fact]
    public async Task IsRunning_TrueDuringBuild_AndConcurrentCallsThrow()
    {
        await using var bench = TestBench.Create(_model);
        if (bench is null) return;

        var path = bench.WriteEpub(SyntheticEpub3Spines());

        var first = bench.IndexingService.IndexBookAsync(path, progress: null, ct: default);

        // Without yielding, the async method may have already completed for a tiny EPUB,
        // so this test is best-effort. The InvalidOperationException is the contract.
        if (bench.IndexingService.IsRunning)
        {
            var act = async () => await bench.IndexingService.IndexBookAsync(
                path, progress: null, ct: default);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        await first;
        bench.IndexingService.IsRunning.Should().BeFalse();
    }

    private static async Task<IReadOnlyList<SearchHit>> SearchByText(TestBench bench, string text)
    {
        var embedding = await bench.Embedder.EmbedAsync(new[] { text });
        return await bench.Store.SearchAsync(embedding[0], k: 5);
    }

    /// <summary>
    /// 3-spine EPUB with topically distinct chapters so semantic search can prove
    /// the chunks back-link to the right spine item.
    /// </summary>
    private static byte[] SyntheticEpub3Spines() => BuildEpub(
        ("META-INF/container.xml", Container("OEBPS/content.opf")),
        ("OEBPS/content.opf", OpfWith3Chapters()),
        ("OEBPS/nav.xhtml", Nav()),
        ("OEBPS/ch01.xhtml", Chapter("On Cats",
            "<p>The domestic cat is a small carnivorous mammal kept as a household pet. " +
            "Cats have soft fur, retractable claws, and acute senses for hunting small prey. " +
            "They communicate through purring, meowing, hissing, and body language.</p>")),
        ("OEBPS/ch02.xhtml", Chapter("On Trains",
            "<p>A train is a series of connected vehicles that runs along a railway track. " +
            "Locomotives pull passenger cars and freight wagons across long distances. " +
            "Steam, diesel, and electric trains have shaped industrial transport for two centuries.</p>")),
        ("OEBPS/ch03.xhtml", Chapter("On Quantum Physics",
            "<p>Quantum mechanics describes the behavior of matter and energy at subatomic scales. " +
            "Particles like electrons exhibit both wave and particle properties depending on observation. " +
            "Superposition, entanglement, and uncertainty are the strange foundations of the field.</p>")));

    private static byte[] BuildEpub(params (string Path, string Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var mimetype = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var w = new StreamWriter(mimetype.Open(), new UTF8Encoding(false)))
                w.Write("application/epub+zip");

            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static string Container(string opfPath) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
          <rootfiles>
            <rootfile full-path="{{opfPath}}" media-type="application/oebps-package+xml"/>
          </rootfiles>
        </container>
        """;

    private static string OpfWith3Chapters() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <package version="3.0" unique-identifier="bookid" xmlns="http://www.idpf.org/2007/opf">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
            <dc:title>Test Anthology</dc:title>
            <dc:creator>Test Author</dc:creator>
            <dc:identifier id="bookid">urn:uuid:test-anthology</dc:identifier>
            <dc:language>en</dc:language>
          </metadata>
          <manifest>
            <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
            <item id="ch1" href="ch01.xhtml" media-type="application/xhtml+xml"/>
            <item id="ch2" href="ch02.xhtml" media-type="application/xhtml+xml"/>
            <item id="ch3" href="ch03.xhtml" media-type="application/xhtml+xml"/>
          </manifest>
          <spine>
            <itemref idref="ch1"/>
            <itemref idref="ch2"/>
            <itemref idref="ch3"/>
          </spine>
        </package>
        """;

    private static string Nav() => """
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
          <head><title>Contents</title></head>
          <body>
            <nav epub:type="toc">
              <ol>
                <li><a href="ch01.xhtml">Cats</a></li>
                <li><a href="ch02.xhtml">Trains</a></li>
                <li><a href="ch03.xhtml">Physics</a></li>
              </ol>
            </nav>
          </body>
        </html>
        """;

    private static string Chapter(string title, string body) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <html xmlns="http://www.w3.org/1999/xhtml">
          <head><title>{{title}}</title></head>
          <body><h1>{{title}}</h1>{{body}}</body>
        </html>
        """;

    private sealed class TestBench : IAsyncDisposable
    {
        public string TempDir { get; }
        public EmbeddingStore Store { get; }
        public MiniLmEmbedder Embedder { get; }
        public IndexingService IndexingService { get; }

        private TestBench(string tempDir, EmbeddingStore store, MiniLmEmbedder embedder)
        {
            TempDir = tempDir;
            Store = store;
            Embedder = embedder;
            IndexingService = new IndexingService(store, embedder);
        }

        public static TestBench? Create(ModelFixture fixture)
        {
            if (!fixture.IsAvailable) return null;

            var temp = Path.Combine(Path.GetTempPath(),
                "epub-search-indexing-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);

            var dbPath = Path.Combine(temp, "library.db");
            var store = new EmbeddingStore(dbPath);
            return new TestBench(temp, store, fixture.Embedder!);
        }

        public string WriteEpub(byte[] bytes, string fileName = "test.epub")
        {
            var path = Path.Combine(TempDir, fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public ValueTask DisposeAsync()
        {
            // Embedder is owned by ModelFixture; don't dispose here.
            try { Directory.Delete(TempDir, recursive: true); }
            catch { /* best effort */ }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();
        public void Report(T value) { lock (Reports) Reports.Add(value); }
    }
}
