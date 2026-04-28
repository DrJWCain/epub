using System.Text;
using Epub.Search.Embedding;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace Epub.Search.Llm;

/// <summary>
/// Local Phi-4-mini-instruct generator. Downloads ~4.86 GB on first use
/// (gated by user opt-in in Settings) and runs CPU inference via ONNX Runtime
/// GenAI. Wraps the chat template so callers pass plain prompts.
/// Single-instance owns the loaded Model + Tokenizer for the app lifetime.
/// 128 k context window — large enough to send 100+ candidate passages in
/// a single prompt for the concept-thread pipeline.
/// </summary>
public sealed class Phi4Generator : IDisposable
{
    private readonly Phi4ModelDownloader _downloader;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private Model? _model;
    private Tokenizer? _tokenizer;
    private volatile bool _ready;
    private bool _disposed;

    public Phi4Generator(Phi4ModelDownloader downloader)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        _downloader = downloader;
    }

    public bool IsReady => _ready;

    public async Task EnsureReadyAsync(
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (_ready) return;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ready) return;
            await _downloader.EnsureDownloadedAsync(progress, ct).ConfigureAwait(false);
            _model = new Model(_downloader.ModelDir);
            _tokenizer = new Tokenizer(_model);
            _ready = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Generate a completion for the given prompt. Wraps the prompt in Phi-4's
    /// chat template (&lt;|user|&gt; / &lt;|assistant|&gt; — same shape as Phi-3)
    /// before encoding so callers don't have to. Streaming chunks are pushed
    /// through <paramref name="onToken"/> if provided — useful for incremental UI.
    /// </summary>
    public async Task<string> GenerateAsync(
        string prompt,
        int maxTokens = 4096,
        double temperature = 0.5,
        Action<string>? onToken = null,
        CancellationToken ct = default)
    {
        ThrowIfNotReady();
        return await Task.Run(() => GenerateSync(prompt, maxTokens, temperature, onToken, ct), ct)
            .ConfigureAwait(false);
    }

    private string GenerateSync(
        string prompt, int maxTokens, double temperature,
        Action<string>? onToken, CancellationToken ct)
    {
        var fullPrompt = $"<|user|>\n{prompt}<|end|>\n<|assistant|>\n";

        using var sequences = _tokenizer!.Encode(fullPrompt);
        using var generatorParams = new GeneratorParams(_model!);
        generatorParams.SetSearchOption("max_length", maxTokens);
        generatorParams.SetSearchOption("temperature", temperature);

        using var generator = new Generator(_model!, generatorParams);
        generator.AppendTokenSequences(sequences);

        using var stream = _tokenizer.CreateStream();
        var output = new StringBuilder();
        while (!generator.IsDone())
        {
            ct.ThrowIfCancellationRequested();
            generator.GenerateNextToken();
            var seq = generator.GetSequence(0);
            var lastToken = seq[seq.Length - 1];
            var fragment = stream.Decode(lastToken);
            if (!string.IsNullOrEmpty(fragment))
                output.Append(fragment);
            // Always invoke onToken so callers see real per-token progress.
            // BPE decoders frequently emit empty fragments for partial-byte
            // tokens, so gating onToken on non-empty fragments would make
            // the count climb erratically (or not at all for long stretches).
            onToken?.Invoke(fragment ?? string.Empty);
        }

        return output.ToString().Trim();
    }

    private void ThrowIfNotReady()
    {
        if (!_ready)
            throw new InvalidOperationException(
                "Phi4Generator has not been initialised. Call EnsureReadyAsync() first.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tokenizer?.Dispose();
        _model?.Dispose();
        _initLock.Dispose();
    }
}
