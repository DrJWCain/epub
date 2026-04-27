using System.Net;
using System.Text;
using Epub.Search.Embedding;
using FluentAssertions;

namespace Epub.Search.Tests;

public sealed class MiniLmModelDownloaderTests
{
    [Fact]
    public async Task IsDownloaded_WhenBothFilesPresent_ReturnsTrue()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "model.onnx"), "x");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "vocab.txt"), "y");

        var dl = new MiniLmModelDownloader(temp.Path);

        dl.IsDownloaded.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureDownloaded_WhenFilesAlreadyPresent_MakesNoHttpRequests()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "model.onnx"), "fake");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "vocab.txt"), "fake");

        var handler = new TrackingHandler();
        using var http = new HttpClient(handler);
        var dl = new MiniLmModelDownloader(temp.Path, http);

        await dl.EnsureDownloadedAsync(progress: null, ct: default);

        handler.RequestCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureDownloaded_FetchesBothFiles_AndAtomicallyRenames()
    {
        using var temp = new TempDir();
        var modelBytes = new byte[] { 1, 2, 3, 4, 5 };
        var vocabBytes = Encoding.UTF8.GetBytes("vocab content");

        var handler = new CannedHandler(new Dictionary<Uri, byte[]>
        {
            [MiniLmModelDownloader.ModelUrl] = modelBytes,
            [MiniLmModelDownloader.VocabUrl] = vocabBytes,
        });
        using var http = new HttpClient(handler);
        var dl = new MiniLmModelDownloader(temp.Path, http);

        await dl.EnsureDownloadedAsync(progress: null, ct: default);

        File.Exists(dl.ModelPath).Should().BeTrue();
        File.Exists(dl.VocabPath).Should().BeTrue();
        File.Exists(dl.ModelPath + ".part").Should().BeFalse();
        File.Exists(dl.VocabPath + ".part").Should().BeFalse();
        (await File.ReadAllBytesAsync(dl.ModelPath)).Should().Equal(modelBytes);
        (await File.ReadAllBytesAsync(dl.VocabPath)).Should().Equal(vocabBytes);
    }

    [Fact]
    public async Task EnsureDownloaded_FetchesOnlyMissingFile_WhenOneAlreadyPresent()
    {
        using var temp = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "vocab.txt"), "preexisting");

        var modelBytes = new byte[] { 9, 9, 9 };
        var handler = new CannedHandler(new Dictionary<Uri, byte[]>
        {
            [MiniLmModelDownloader.ModelUrl] = modelBytes,
        });
        using var http = new HttpClient(handler);
        var dl = new MiniLmModelDownloader(temp.Path, http);

        await dl.EnsureDownloadedAsync(progress: null, ct: default);

        handler.RequestedUrls.Should().ContainSingle()
            .Which.Should().Be(MiniLmModelDownloader.ModelUrl);
        (await File.ReadAllBytesAsync(dl.ModelPath)).Should().Equal(modelBytes);
        (await File.ReadAllTextAsync(dl.VocabPath)).Should().Be("preexisting");
    }

    [Fact]
    public async Task EnsureDownloaded_ReportsProgress_ForEachDownloadedFile()
    {
        using var temp = new TempDir();
        var handler = new CannedHandler(new Dictionary<Uri, byte[]>
        {
            [MiniLmModelDownloader.ModelUrl] = new byte[5000],
            [MiniLmModelDownloader.VocabUrl] = new byte[100],
        });
        using var http = new HttpClient(handler);
        var dl = new MiniLmModelDownloader(temp.Path, http);

        var progress = new CapturingProgress<DownloadProgress>();
        await dl.EnsureDownloadedAsync(progress, ct: default);

        progress.Reports.Should().NotBeEmpty();
        progress.Reports.Should().Contain(p => p.FileName == "vocab.txt");
        progress.Reports.Should().Contain(p => p.FileName == "model.onnx");
        var lastModel = progress.Reports.Last(p => p.FileName == "model.onnx");
        lastModel.BytesDone.Should().Be(5000);
        lastModel.TotalBytes.Should().Be(5000);
    }

    [Fact]
    public async Task EnsureDownloaded_OnHttpError_DoesNotLeaveFinalFile()
    {
        using var temp = new TempDir();
        var handler = new CannedHandler(new Dictionary<Uri, byte[]>());  // any URL → 404
        using var http = new HttpClient(handler);
        var dl = new MiniLmModelDownloader(temp.Path, http);

        var act = async () => await dl.EnsureDownloadedAsync(progress: null, ct: default);
        await act.Should().ThrowAsync<HttpRequestException>();

        File.Exists(dl.ModelPath).Should().BeFalse();
        File.Exists(dl.VocabPath).Should().BeFalse();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "epub-search-dl-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly Dictionary<Uri, byte[]> _data;
        public List<Uri> RequestedUrls { get; } = new();

        public CannedHandler(Dictionary<Uri, byte[]> data) => _data = data;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!);
            if (!_data.TryGetValue(request.RequestUri!, out var bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
        }
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();
        public void Report(T value) { lock (Reports) Reports.Add(value); }
    }
}
