using Epub.Search.Embedding;

namespace Epub.Search.Llm;

/// <summary>
/// Fetches Microsoft's Phi-4-mini-instruct CPU INT4 ONNX bundle from
/// Hugging Face into a local cache directory on first use. ~4.86 GB across
/// 10 files (the model.onnx.data weight blob alone is 4.87 GB). Atomic
/// per-file via .part rename. Idempotent — re-call no-ops once everything
/// is present.
/// </summary>
/// <remarks>
/// Phi-4-mini was picked over Phi-3-mini-4k for two reasons: (a) 128 k
/// context window (vs 4 k) means we can send 100+ candidate passages
/// instead of being limited to ~30, and (b) better quality on instruction-
/// following + reasoning, which matters for "pick which passages tell a
/// coherent story" prompts. Trade-off is a 4.86 GB download vs 2.73 GB.
/// </remarks>
public sealed class Phi4ModelDownloader
{
    private const string BaseUrl =
        "https://huggingface.co/microsoft/Phi-4-mini-instruct-onnx/resolve/main/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/";

    private static readonly string[] RequiredFiles =
    {
        "model.onnx",
        "model.onnx.data",
        "genai_config.json",
        "config.json",
        "tokenizer.json",
        "tokenizer_config.json",
        "vocab.json",
        "merges.txt",
        "special_tokens_map.json",
        "added_tokens.json",
    };

    private const int BufferSize = 81920;

    private readonly string _cacheDir;
    private readonly HttpClient _http;

    public Phi4ModelDownloader(string cacheDir, HttpClient? http = null)
    {
        _cacheDir = cacheDir;
        _http = http ?? new HttpClient();
    }

    /// <summary>Directory containing the unpacked model files. Pass to
    /// <c>new Model(...)</c> once <see cref="IsDownloaded"/> is true.</summary>
    public string ModelDir => _cacheDir;

    public bool IsDownloaded
    {
        get
        {
            foreach (var f in RequiredFiles)
                if (!File.Exists(Path.Combine(_cacheDir, f))) return false;
            return true;
        }
    }

    public async Task EnsureDownloadedAsync(
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_cacheDir);
        foreach (var file in RequiredFiles)
        {
            var dest = Path.Combine(_cacheDir, file);
            if (File.Exists(dest)) continue;
            await DownloadAsync(BaseUrl + file, dest, file, progress, ct).ConfigureAwait(false);
        }
    }

    private async Task DownloadAsync(string url, string destPath, string displayName,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var tempPath = destPath + ".part";
        // Resume from any prior .part bytes via HTTP Range. HuggingFace's CDN
        // honours Range requests, and Phi-4 weights are big enough that a
        // re-start-from-zero is genuinely painful.
        long startByte = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (startByte > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startByte, null);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // If the server doesn't honour the range (returns 200 instead of 206),
        // restart from scratch.
        if (startByte > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            startByte = 0;
        }
        response.EnsureSuccessStatusCode();

        long remaining = response.Content.Headers.ContentLength ?? 0;
        long totalBytes = startByte + remaining;
        progress?.Report(new DownloadProgress(displayName, startByte, totalBytes));

        var fileMode = startByte > 0 ? FileMode.Append : FileMode.Create;
        await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(tempPath, fileMode, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            var buffer = new byte[BufferSize];
            long bytesDone = startByte;
            int read;

            // Throttle progress reports to ~4/sec. Without throttling, a
            // ~4.87 GB file at 80 KB/chunk fires ~60,000 IProgress callbacks
            // — Progress<T> hops them all to the UI sync context and the UI
            // thread starves, looking frozen.
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while ((read = await src.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                bytesDone += read;
                if (sw.ElapsedMilliseconds >= 250)
                {
                    progress?.Report(new DownloadProgress(displayName, bytesDone, totalBytes));
                    sw.Restart();
                }
            }
            // Final tick at exact total so the bar fills to 100%.
            progress?.Report(new DownloadProgress(displayName, bytesDone, totalBytes));
        }

        File.Move(tempPath, destPath, overwrite: true);
    }
}
