namespace Epub.Search.Embedding;

/// <summary>
/// Fetches the MiniLM ONNX model and its tokenizer vocab from Hugging Face into a
/// local cache directory on first use. Idempotent: subsequent calls no-op if the
/// expected files are already present. Atomic via .part rename.
/// </summary>
public sealed class MiniLmModelDownloader
{
    public static readonly Uri ModelUrl = new(
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx");
    public static readonly Uri VocabUrl = new(
        "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt");

    private const int BufferSize = 81920;

    private readonly string _cacheDir;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public MiniLmModelDownloader(string cacheDir, HttpClient? http = null)
    {
        _cacheDir = cacheDir;
        _http = http ?? new HttpClient();
        _ownsHttp = http is null;
    }

    public string ModelPath => Path.Combine(_cacheDir, "model.onnx");
    public string VocabPath => Path.Combine(_cacheDir, "vocab.txt");
    public bool IsDownloaded => File.Exists(ModelPath) && File.Exists(VocabPath);

    public async Task EnsureDownloadedAsync(
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_cacheDir);

        if (!File.Exists(VocabPath))
            await DownloadAsync(VocabUrl, VocabPath, "vocab.txt", progress, ct).ConfigureAwait(false);

        if (!File.Exists(ModelPath))
            await DownloadAsync(ModelUrl, ModelPath, "model.onnx", progress, ct).ConfigureAwait(false);
    }

    private async Task DownloadAsync(Uri url, string destPath, string displayName,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var tempPath = destPath + ".part";
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        progress?.Report(new DownloadProgress(displayName, 0, totalBytes));

        await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(tempPath))
        {
            var buffer = new byte[BufferSize];
            long bytesDone = 0;
            int read;
            while ((read = await src.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                bytesDone += read;
                progress?.Report(new DownloadProgress(displayName, bytesDone, totalBytes));
            }
        }

        File.Move(tempPath, destPath, overwrite: true);
    }
}

public readonly record struct DownloadProgress(string FileName, long BytesDone, long TotalBytes);
