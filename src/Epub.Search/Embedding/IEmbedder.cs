namespace Epub.Search.Embedding;

/// <summary>
/// Produces L2-normalized 384-dimension embeddings for batches of strings.
/// Implementations may need to be initialized (e.g. by downloading a model)
/// before use; see <see cref="MiniLmEmbedder.EnsureReadyAsync"/>.
/// </summary>
public interface IEmbedder
{
    /// <summary>The dimensionality of every embedding this embedder produces.</summary>
    int Dimension { get; }

    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
