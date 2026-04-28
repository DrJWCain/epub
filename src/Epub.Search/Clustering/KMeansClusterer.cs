using System.Numerics;

namespace Epub.Search.Clustering;

/// <summary>
/// Spherical K-means for L2-normalized embeddings: assignment by maximum dot
/// product (== cosine similarity for unit vectors), centroids re-normalized
/// to unit length on each iteration. Initialization is k-means++.
/// Substitute for HDBSCAN at v1; HDBSCAN .NET ports are flaky and we need
/// reproducibility for tests. K is supplied by the caller.
/// </summary>
public sealed class KMeansClusterer
{
    public int K { get; }
    public int MaxIterations { get; }
    public int? Seed { get; }

    public KMeansClusterer(int k, int maxIterations = 30, int? seed = null)
    {
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));
        if (maxIterations <= 0) throw new ArgumentOutOfRangeException(nameof(maxIterations));
        K = k;
        MaxIterations = maxIterations;
        Seed = seed;
    }

    public KMeansResult Cluster(
        IReadOnlyList<float[]> vectors,
        IProgress<KMeansProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (vectors.Count == 0)
            return new KMeansResult(Array.Empty<float[]>(), Array.Empty<int>());

        int dim = vectors[0].Length;
        int actualK = Math.Min(K, vectors.Count);

        var rng = Seed.HasValue ? new Random(Seed.Value) : new Random();
        var centroids = KMeansPlusPlusInit(vectors, actualK, rng, ct);
        var assignments = new int[vectors.Count];

        int reassignments = vectors.Count;
        for (int iter = 0; iter < MaxIterations; iter++)
        {
            ct.ThrowIfCancellationRequested();
            reassignments = AssignAll(vectors, centroids, assignments);
            progress?.Report(new KMeansProgress(iter + 1, MaxIterations, reassignments));
            if (reassignments == 0) break;
            RecomputeCentroids(vectors, assignments, centroids, dim);
        }

        return new KMeansResult(centroids, assignments);
    }

    private static float[][] KMeansPlusPlusInit(
        IReadOnlyList<float[]> vectors, int k, Random rng, CancellationToken ct)
    {
        var centroids = new float[k][];
        // First centroid: pick uniformly at random.
        centroids[0] = (float[])vectors[rng.Next(vectors.Count)].Clone();

        var minDistSq = new double[vectors.Count];
        Array.Fill(minDistSq, double.PositiveInfinity);

        for (int i = 1; i < k; i++)
        {
            ct.ThrowIfCancellationRequested();
            // For each vector, update its squared distance to the nearest centroid so far.
            // For unit vectors, ||a-b||² = 2 - 2·(a·b), so maximize dot to minimise distance².
            var newCentroid = centroids[i - 1];
            double total = 0;
            for (int j = 0; j < vectors.Count; j++)
            {
                var d = 2.0 - 2.0 * Dot(vectors[j], newCentroid);
                if (d < minDistSq[j]) minDistSq[j] = d;
                total += minDistSq[j];
            }
            // Pick next centroid weighted by minDistSq.
            var pick = rng.NextDouble() * total;
            double running = 0;
            int chosen = vectors.Count - 1;
            for (int j = 0; j < vectors.Count; j++)
            {
                running += minDistSq[j];
                if (running >= pick) { chosen = j; break; }
            }
            centroids[i] = (float[])vectors[chosen].Clone();
        }
        return centroids;
    }

    private static int AssignAll(
        IReadOnlyList<float[]> vectors, float[][] centroids, int[] assignments)
    {
        int reassigned = 0;
        for (int i = 0; i < vectors.Count; i++)
        {
            float bestSim = float.NegativeInfinity;
            int bestK = 0;
            for (int k = 0; k < centroids.Length; k++)
            {
                var sim = Dot(vectors[i], centroids[k]);
                if (sim > bestSim) { bestSim = sim; bestK = k; }
            }
            if (assignments[i] != bestK)
            {
                assignments[i] = bestK;
                reassigned++;
            }
        }
        return reassigned;
    }

    private static void RecomputeCentroids(
        IReadOnlyList<float[]> vectors, int[] assignments, float[][] centroids, int dim)
    {
        var sums = new float[centroids.Length][];
        var counts = new int[centroids.Length];
        for (int k = 0; k < centroids.Length; k++) sums[k] = new float[dim];

        for (int i = 0; i < vectors.Count; i++)
        {
            var k = assignments[i];
            var v = vectors[i];
            var s = sums[k];
            for (int d = 0; d < dim; d++) s[d] += v[d];
            counts[k]++;
        }

        for (int k = 0; k < centroids.Length; k++)
        {
            if (counts[k] == 0)
            {
                // Empty cluster — keep the previous centroid (rare with k-means++).
                continue;
            }
            float invN = 1f / counts[k];
            float ssq = 0;
            for (int d = 0; d < dim; d++)
            {
                sums[k][d] *= invN;
                ssq += sums[k][d] * sums[k][d];
            }
            float norm = MathF.Sqrt(ssq);
            if (norm > 0)
            {
                float invNorm = 1f / norm;
                for (int d = 0; d < dim; d++) sums[k][d] *= invNorm;
            }
            centroids[k] = sums[k];
        }
    }

    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float dot = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            int simdEnd = a.Length - (a.Length % Vector<float>.Count);
            for (; i < simdEnd; i += Vector<float>.Count)
            {
                acc += new Vector<float>(a.Slice(i, Vector<float>.Count))
                     * new Vector<float>(b.Slice(i, Vector<float>.Count));
            }
            dot = Vector.Dot(acc, Vector<float>.One);
        }
        for (; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }
}

public sealed record KMeansResult(float[][] Centroids, int[] Assignments);
public sealed record KMeansProgress(int Iteration, int MaxIterations, int Reassignments);
