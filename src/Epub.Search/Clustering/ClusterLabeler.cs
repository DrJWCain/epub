using System.Text;

namespace Epub.Search.Clustering;

/// <summary>
/// Class-based TF-IDF labeler (a la BERTopic): each cluster is treated as a
/// single document, term frequencies are computed per cluster, and IDF uses
/// the cluster set as the document corpus. Top-N terms by TF·IDF become the
/// cluster's label — they are by construction the words that distinguish
/// THIS cluster from the rest.
/// </summary>
public sealed class ClusterLabeler
{
    public int TopN { get; }
    public int MinTokenLength { get; }

    public ClusterLabeler(int topN = 4, int minTokenLength = 3)
    {
        if (topN <= 0) throw new ArgumentOutOfRangeException(nameof(topN));
        if (minTokenLength < 1) throw new ArgumentOutOfRangeException(nameof(minTokenLength));
        TopN = topN;
        MinTokenLength = minTokenLength;
    }

    public IReadOnlyDictionary<long, string> ComputeLabels(
        IEnumerable<(long ClusterId, string Text)> clusterChunks)
    {
        // Pass 1 — per-cluster term counts.
        var clusterTerms = new Dictionary<long, Dictionary<string, int>>();
        foreach (var (clusterId, text) in clusterChunks)
        {
            if (!clusterTerms.TryGetValue(clusterId, out var counts))
            {
                counts = new Dictionary<string, int>(StringComparer.Ordinal);
                clusterTerms[clusterId] = counts;
            }
            foreach (var term in Tokenize(text))
            {
                counts.TryGetValue(term, out var c);
                counts[term] = c + 1;
            }
        }

        // Pass 2 — document frequency over the cluster set.
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, counts) in clusterTerms)
        {
            foreach (var term in counts.Keys)
            {
                df.TryGetValue(term, out var d);
                df[term] = d + 1;
            }
        }

        var nClusters = clusterTerms.Count;
        var labels = new Dictionary<long, string>(nClusters);

        // Drop terms that appear in too many clusters — they're by definition
        // not distinctive of any one. Skip the cap when there are too few
        // clusters (the cap would otherwise filter everything for small N).
        bool applyDfCap = nClusters >= 5;
        int dfMax = (int)Math.Floor(nClusters * 0.7);

        foreach (var (clusterId, counts) in clusterTerms)
        {
            int clusterTotal = 0;
            foreach (var v in counts.Values) clusterTotal += v;
            if (clusterTotal == 0)
            {
                labels[clusterId] = string.Empty;
                continue;
            }

            // Smoothed IDF: log(N/df) + 1. Always ≥ 1 when df ≤ N, so even the
            // small-corpus case (where vanilla log(N/(1+df)) goes negative)
            // produces meaningful relative scoring.
            var scored = counts
                .Where(kv => !applyDfCap || df[kv.Key] <= dfMax)
                .Select(kv =>
                {
                    var tf = (double)kv.Value / clusterTotal;
                    var idf = Math.Log((double)nClusters / Math.Max(df[kv.Key], 1)) + 1;
                    return (Term: kv.Key, Score: tf * idf);
                })
                .OrderByDescending(s => s.Score)
                .Take(TopN)
                .Select(s => s.Term);

            labels[clusterId] = string.Join(" · ", scored);
        }

        return labels;
    }

    private IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (sb.Length > 0)
            {
                var token = sb.ToString();
                sb.Clear();
                if (token.Length >= MinTokenLength && !Stopwords.Contains(token))
                    yield return token;
            }
        }
        if (sb.Length > 0)
        {
            var token = sb.ToString();
            if (token.Length >= MinTokenLength && !Stopwords.Contains(token))
                yield return token;
        }
    }

    // Standard English stopwords, plus a few common technical-prose fillers
    // (e.g. "use", "using") that show up in every chapter and add no signal.
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "was", "were", "been", "being", "have", "has", "had",
        "this", "that", "these", "those", "with", "from", "into", "onto", "upon", "than",
        "then", "they", "them", "their", "there", "where", "when", "what", "which", "who",
        "whom", "whose", "why", "how", "all", "any", "both", "each", "few", "more", "most",
        "other", "some", "such", "nor", "not", "only", "own", "same", "too", "very", "just",
        "now", "also", "again", "further", "once", "here", "still", "yet", "but", "him",
        "her", "his", "its", "our", "your", "ours", "yours", "theirs", "itself", "myself",
        "yourself", "himself", "herself", "themselves", "ourselves", "you", "she", "out",
        "off", "down", "over", "under", "until", "while", "before", "after", "above", "below",
        "between", "during", "within", "without", "against", "about", "across", "along",
        "around", "among", "may", "might", "must", "shall", "should", "would", "could",
        "will", "can", "does", "did", "doing", "done",
        "use", "used", "using", "uses", "see", "seen", "saw", "make", "made", "making",
        "get", "got", "given", "give", "gives", "gave", "take", "took", "taken", "taking",
        "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "first", "second", "third", "next", "last", "many", "much", "such", "even", "ever",
        "every", "either", "neither", "any", "anyone", "anything", "everyone", "everything",
        "someone", "something", "nothing", "noone", "another",
        // Programming-language keywords. The user's library is heavily technical;
        // every chapter has code samples and these tokens otherwise dominate the
        // cluster labels regardless of topic.
        "self", "def", "class", "return", "lambda", "raise", "yield", "async", "await",
        "import", "none", "true", "false", "null", "undefined", "this", "new", "delete",
        "function", "func", "var", "let", "const", "static", "final", "public", "private",
        "protected", "internal", "abstract", "virtual", "override", "sealed", "interface",
        "struct", "void", "int", "char", "float", "double", "bool", "string", "long", "short",
        "object", "type", "args", "kwargs", "param", "params", "arg", "elif", "endif",
        "endfor", "endwhile", "switch", "case", "break", "continue", "throw", "throws",
        "catch", "finally", "try", "except",
    };
}
