using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
        // The model often emits valid JSON followed by trailing commentary
        // (sometimes with embedded braces), or wraps the JSON in markdown
        // fences, or — for off-template queries — drops the wrapping object
        // and writes a bare array. Walk forward from the first opener,
        // tracking depth, to extract the first balanced block; try {…} then
        // [...]. Inside, accept several plausible root keys and id fields.
        var objectJson = ExtractFirstBalanced(llmOutput, '{', '}');
        if (objectJson is not null)
        {
            var steps = TryParseObject(objectJson, hits);
            if (steps.Count > 0) return steps;
        }

        var arrayJson = ExtractFirstBalanced(llmOutput, '[', ']');
        if (arrayJson is not null)
        {
            var steps = TryParseArray(arrayJson, hits);
            if (steps.Count > 0) return steps;
        }

        return Array.Empty<ConceptThreadStep>();
    }

    private static string? ExtractFirstBalanced(string text, char open, char close)
    {
        var start = text.IndexOf(open);
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (inString)
            {
                if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return text.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    private static IReadOnlyList<ConceptThreadStep> TryParseObject(
        string json, IReadOnlyList<SearchHit> hits)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, JsonReaderOptions);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array) return ParseStepArray(root, hits);
            if (root.ValueKind != JsonValueKind.Object) return Array.Empty<ConceptThreadStep>();
            // First pass: known root keys.
            foreach (var name in new[] { "thread", "passages", "steps", "sequence", "items" })
            {
                if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
                {
                    var steps = ParseStepArray(prop, hits);
                    if (steps.Count > 0) return steps;
                }
            }
            // Second pass: any array value the model named differently.
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                var steps = ParseStepArray(prop.Value, hits);
                if (steps.Count > 0) return steps;
            }
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[ConceptThreadService] JSON object parse failed: {ex.Message}");
        }
        return Array.Empty<ConceptThreadStep>();
    }

    private static IReadOnlyList<ConceptThreadStep> TryParseArray(
        string json, IReadOnlyList<SearchHit> hits)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, JsonReaderOptions);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return ParseStepArray(doc.RootElement, hits);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[ConceptThreadService] JSON array parse failed: {ex.Message}");
        }
        return Array.Empty<ConceptThreadStep>();
    }

    private static IReadOnlyList<ConceptThreadStep> ParseStepArray(
        JsonElement array, IReadOnlyList<SearchHit> hits)
    {
        var steps = new List<ConceptThreadStep>();
        foreach (var element in array.EnumerateArray())
        {
            int? id;
            string? transition;
            if (element.ValueKind == JsonValueKind.Number)
            {
                // Tolerate a bare-number array like [3, 17, 42] as a degenerate shape.
                id = element.TryGetInt32(out var n) ? n : null;
                transition = null;
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                id = TryGetInt(element, "id")
                    ?? TryGetInt(element, "passage_id")
                    ?? TryGetInt(element, "passage")
                    ?? TryGetInt(element, "number")
                    ?? TryGetInt(element, "n");
                transition = TryGetString(element, "transition")
                    ?? TryGetString(element, "intro")
                    ?? TryGetString(element, "bridge")
                    ?? TryGetString(element, "comment");
            }
            else continue;

            if (id is null || id < 1 || id > hits.Count) continue;
            steps.Add(new ConceptThreadStep(hits[id.Value - 1], transition ?? string.Empty));
        }
        return steps;
    }

    private static int? TryGetInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var n)) return n;
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var ns)) return ns;
        return null;
    }

    private static string? TryGetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() : null;

    private static readonly JsonDocumentOptions JsonReaderOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };
}

public sealed record ConceptThread(
    string Query,
    IReadOnlyList<ConceptThreadStep> Steps,
    string RawLlmOutput);

public sealed record ConceptThreadStep(SearchHit Passage, string Transition);

public readonly record struct ThreadProgress(string Phase);
