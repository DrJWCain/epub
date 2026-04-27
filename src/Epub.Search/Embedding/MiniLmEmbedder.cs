using Epub.Search.Chunking;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Epub.Search.Embedding;

/// <summary>
/// Local sentence-transformers MiniLM embedder. Downloads the model on first use
/// (see <see cref="EnsureReadyAsync"/>) and runs CPU inference via ONNX Runtime.
/// Implements both <see cref="IEmbedder"/> (for the indexing pipeline) and
/// <see cref="ITokenCounter"/> (for the chunker) so a single instance powers
/// chunking and embedding with consistent tokenization.
/// </summary>
public sealed class MiniLmEmbedder : IEmbedder, ITokenCounter, IDisposable
{
    public const int EmbeddingDimension = 384;
    public const int MaxSequenceLength = 512;
    public const int DefaultBatchSize = 32;

    private readonly MiniLmModelDownloader _downloader;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private volatile bool _ready;

    public MiniLmEmbedder(MiniLmModelDownloader downloader)
    {
        ArgumentNullException.ThrowIfNull(downloader);
        _downloader = downloader;
    }

    public bool IsReady => _ready;
    public int Dimension => EmbeddingDimension;

    /// <summary>
    /// Download the model files if needed and warm up the InferenceSession.
    /// Safe to call multiple times; only the first call does work.
    /// </summary>
    public async Task EnsureReadyAsync(
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (_ready) return;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ready) return;
            await _downloader.EnsureDownloadedAsync(progress, ct).ConfigureAwait(false);
            _session = new InferenceSession(_downloader.ModelPath);
            _tokenizer = BertTokenizer.Create(_downloader.VocabPath);
            _ready = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public int CountTokens(string text)
    {
        ThrowIfNotReady();
        return _tokenizer!.CountTokens(text);
    }

    public async Task<float[][]> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        ThrowIfNotReady();
        if (texts.Count == 0) return Array.Empty<float[]>();

        // ONNX Runtime is synchronous; offload to thread pool so callers can stay async.
        return await Task.Run(() => EmbedBatch(texts, ct), ct).ConfigureAwait(false);
    }

    private float[][] EmbedBatch(IReadOnlyList<string> texts, CancellationToken ct)
    {
        // 1. Tokenize each text → ids (with special tokens), truncate to MaxSequenceLength.
        var allIds = new int[texts.Count][];
        for (int i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var raw = _tokenizer!.EncodeToIds(texts[i], addSpecialTokens: true, considerPreTokenization: true);
            if (raw.Count > MaxSequenceLength)
            {
                var truncated = new int[MaxSequenceLength];
                for (int j = 0; j < MaxSequenceLength - 1; j++) truncated[j] = raw[j];
                // Preserve trailing [SEP] when truncating.
                truncated[MaxSequenceLength - 1] = _tokenizer.SeparatorTokenId;
                allIds[i] = truncated;
            }
            else
            {
                allIds[i] = raw.ToArray();
            }
        }

        int batch = texts.Count;
        int maxLen = 0;
        for (int i = 0; i < batch; i++)
            if (allIds[i].Length > maxLen) maxLen = allIds[i].Length;
        if (maxLen == 0) maxLen = 1;  // degenerate but defensive

        // 2. Build padded int64 input tensors. Padding is the tokenizer's [PAD] id;
        //    attention_mask is 1 for real tokens and 0 for padding.
        var inputIds = new long[batch * maxLen];
        var attentionMask = new long[batch * maxLen];
        var typeIds = new long[batch * maxLen];
        long padId = _tokenizer!.PaddingTokenId;
        for (int b = 0; b < batch; b++)
        {
            var ids = allIds[b];
            int rowStart = b * maxLen;
            for (int s = 0; s < maxLen; s++)
            {
                if (s < ids.Length)
                {
                    inputIds[rowStart + s] = ids[s];
                    attentionMask[rowStart + s] = 1;
                }
                else
                {
                    inputIds[rowStart + s] = padId;
                    attentionMask[rowStart + s] = 0;
                }
                // typeIds stays 0 (single sequence, no sentence-pair task)
            }
        }

        var dims = new[] { batch, maxLen };
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, dims)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, dims)),
            NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(typeIds, dims)),
        };

        using var outputs = _session!.Run(inputs);
        var hidden = outputs.First().AsTensor<float>();
        var hiddenDims = hidden.Dimensions;
        if (hiddenDims.Length != 3 || hiddenDims[0] != batch || hiddenDims[2] != EmbeddingDimension)
            throw new InvalidOperationException(
                $"Unexpected ONNX output shape [{string.Join(',', hiddenDims.ToArray())}]; " +
                $"expected [{batch}, *, {EmbeddingDimension}].");

        int seqOut = hiddenDims[1];

        // 3. Mean-pool over the masked sequence dimension and L2-normalize.
        var result = new float[batch][];
        for (int b = 0; b < batch; b++)
        {
            var pooled = new float[EmbeddingDimension];
            int count = 0;
            for (int s = 0; s < seqOut; s++)
            {
                if (attentionMask[b * maxLen + s] != 1) continue;
                for (int d = 0; d < EmbeddingDimension; d++)
                    pooled[d] += hidden[b, s, d];
                count++;
            }
            if (count > 0)
            {
                float inv = 1f / count;
                for (int d = 0; d < EmbeddingDimension; d++) pooled[d] *= inv;
            }
            float ssq = 0;
            for (int d = 0; d < EmbeddingDimension; d++) ssq += pooled[d] * pooled[d];
            float norm = MathF.Sqrt(ssq);
            if (norm > 0)
            {
                float invNorm = 1f / norm;
                for (int d = 0; d < EmbeddingDimension; d++) pooled[d] *= invNorm;
            }
            result[b] = pooled;
        }

        return result;
    }

    private void ThrowIfNotReady()
    {
        if (!_ready)
            throw new InvalidOperationException(
                "MiniLmEmbedder has not been initialized. Call EnsureReadyAsync() first.");
    }

    public void Dispose()
    {
        _session?.Dispose();
        _initLock.Dispose();
    }
}
