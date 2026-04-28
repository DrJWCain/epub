using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Epub.Search.Embedding;

namespace Epub.Search.Llm;

/// <summary>
/// End-to-end pipeline for the concept-thread feature: embed the user's
/// query, pull the top-K candidate passages from the embedding store, ask
/// Phi-4-mini to pick N of them in a coherent reading order with one-line
/// transitions between, parse the LLM's JSON output back into structured
/// passages.
/// </summary>
public sealed class ConceptThreadService
{
    /// <summary>How many candidate passages to send to the LLM. Phi-4-mini's
    /// 128 k context comfortably holds 100 × ~240-char snippets plus the
    /// instruction wrapper and the generated thread output.</summary>
    public const int CandidatePool = 100;

    private readonly IEmbeddingStore _store;
    private readonly MiniLmEmbedder _embedder;
    private readonly Phi4Generator _generator;

    public ConceptThreadService(
        IEmbeddingStore store, MiniLmEmbedder embedder, Phi4Generator generator)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(generator);
        _store = store;
        _embedder = embedder;
        _generator = generator;
    }

    public Task<long> SaveAsync(ConceptThread thread, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(thread.Steps, JsonOptions);
        return _store.SaveThreadAsync(thread.Query, json, ct);
    }

    public Task<IReadOnlyList<SavedThreadSummary>> ListSavedAsync(CancellationToken ct = default)
        => _store.ListThreadsAsync(ct);

    public async Task<ConceptThread?> LoadSavedAsync(long id, CancellationToken ct = default)
    {
        var json = await _store.GetThreadJsonAsync(id, ct).ConfigureAwait(false);
        if (json is null) return null;
        var summary = (await _store.ListThreadsAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(s => s.Id == id);
        if (summary is null) return null;
        IReadOnlyList<ConceptThreadStep>? steps;
        try
        {
            steps = JsonSerializer.Deserialize<IReadOnlyList<ConceptThreadStep>>(json, JsonOptions);
        }
        catch (JsonException) { return null; }
        return steps is null ? null : new ConceptThread(summary.Query, steps, string.Empty);
    }

    public Task DeleteSavedAsync(long id, CancellationToken ct = default)
        => _store.DeleteThreadAsync(id, ct);

    public async Task<ConceptThread> GenerateAsync(
        string query,
        int targetLength,
        IProgress<ThreadProgress>? progress = null,
        Action<string>? onToken = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query must not be empty.", nameof(query));
        if (targetLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetLength));

        progress?.Report(new ThreadProgress("Embedding query…"));
        await _embedder.EnsureReadyAsync(progress: null, ct).ConfigureAwait(false);
        var queryEmbedding = await _embedder.EmbedAsync(new[] { query }, ct).ConfigureAwait(false);

        progress?.Report(new ThreadProgress("Finding candidate passages…"));
        var hits = await _store.SearchAsync(queryEmbedding[0], CandidatePool, ct).ConfigureAwait(false);
        if (hits.Count == 0)
            return new ConceptThread(query, Array.Empty<ConceptThreadStep>(), string.Empty);

        progress?.Report(new ThreadProgress("Loading AI model (this may download ~4.86 GB on first run)…"));
        await _generator.EnsureReadyAsync(progress: null, ct).ConfigureAwait(false);

        var prompt = BuildPrompt(query, hits, Math.Min(targetLength, hits.Count));

        progress?.Report(new ThreadProgress("Generating thread…"));
        var rawOutput = await _generator
            .GenerateAsync(prompt, maxTokens: 8192, temperature: 0.5, onToken: onToken, ct: ct)
            .ConfigureAwait(false);

        var steps = ParseThreadJson(rawOutput, hits);
        return new ConceptThread(query, steps, rawOutput);
    }

    private static string BuildPrompt(string query, IReadOnlyList<SearchHit> hits, int targetLength)
    {
        var sb = new StringBuilder(8192);
        sb.Append("You are helping a reader build a thematic reading sequence on the topic: \"");
        sb.Append(query);
        sb.Append("\".\n\n");
        sb.Append("Below are ").Append(hits.Count).Append(" passages from across the reader's library, ");
        sb.Append("numbered 1 through ").Append(hits.Count).Append(". Pick ").Append(targetLength);
        sb.Append(" of them that, read in order, form a coherent progression through the topic — ");
        sb.Append("introductory ideas before advanced ones, dependencies before consequences, ");
        sb.Append("simple before complex.\n\n");
        sb.Append("For each chosen passage, write a one-sentence transition that bridges from the ");
        sb.Append("previous passage to this one. The first passage gets a one-sentence opener that ");
        sb.Append("frames the topic, instead.\n\n");
        sb.Append("Output strictly as JSON, with this exact structure (no markdown fences, no ");
        sb.Append("commentary, no extra fields):\n");
        sb.Append("{\n  \"thread\": [\n");
        sb.Append("    { \"id\": <passage number>, \"transition\": \"<one sentence>\" },\n");
        sb.Append("    ...\n  ]\n}\n\n");
        sb.Append("Passages:\n\n");
        for (int i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            var bookName = Path.GetFileNameWithoutExtension(h.BookPath);
            sb.Append('[').Append(i + 1).Append("] (Source: ").Append(bookName).Append(") ");
            sb.Append(h.Snippet).Append("\n\n");
        }
        return sb.ToString();
    }

    private static IReadOnlyList<ConceptThreadStep> ParseThreadJson(
        string llmOutput, IReadOnlyList<SearchHit> hits)
    {
        // Phi-4 occasionally wraps JSON in markdown fences despite the
        // instruction. Strip anything outside the first { ... } pair.
        var braceStart = llmOutput.IndexOf('{');
        var braceEnd = llmOutput.LastIndexOf('}');
        if (braceStart < 0 || braceEnd <= braceStart)
            return Array.Empty<ConceptThreadStep>();

        var json = llmOutput.Substring(braceStart, braceEnd - braceStart + 1);
        ThreadJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ThreadJson>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return Array.Empty<ConceptThreadStep>();
        }
        if (parsed?.Thread is null) return Array.Empty<ConceptThreadStep>();

        var steps = new List<ConceptThreadStep>(parsed.Thread.Count);
        foreach (var step in parsed.Thread)
        {
            if (step.Id < 1 || step.Id > hits.Count) continue;
            var hit = hits[step.Id - 1];
            steps.Add(new ConceptThreadStep(hit, step.Transition ?? string.Empty));
        }
        return steps;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    private sealed class ThreadJson
    {
        [JsonPropertyName("thread")]
        public List<ThreadJsonStep>? Thread { get; set; }
    }

    private sealed class ThreadJsonStep
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
        [JsonPropertyName("transition")]
        public string? Transition { get; set; }
    }
}

public sealed record ConceptThread(
    string Query,
    IReadOnlyList<ConceptThreadStep> Steps,
    string RawLlmOutput);

public sealed record ConceptThreadStep(SearchHit Passage, string Transition);

public readonly record struct ThreadProgress(string Phase);
