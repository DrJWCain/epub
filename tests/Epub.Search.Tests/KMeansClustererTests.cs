using Epub.Search.Clustering;
using FluentAssertions;

namespace Epub.Search.Tests;

public sealed class KMeansClustererTests
{
    [Fact]
    public void Cluster_EmptyInput_ReturnsEmptyResult()
    {
        var k = new KMeansClusterer(3);
        var result = k.Cluster(Array.Empty<float[]>());

        result.Centroids.Should().BeEmpty();
        result.Assignments.Should().BeEmpty();
    }

    [Fact]
    public void Cluster_KExceedsCorpus_ClampsToCorpusSize()
    {
        var k = new KMeansClusterer(10);
        var vectors = new[]
        {
            UnitVector(0, 4),
            UnitVector(1, 4),
        };

        var result = k.Cluster(vectors);

        result.Centroids.Length.Should().Be(2);
        result.Assignments.Length.Should().Be(2);
    }

    [Fact]
    public void Cluster_ThreeWellSeparatedGroups_RecoversAssignments()
    {
        // Three groups of unit vectors near distinct axes. Each "noisy"
        // variant is still much closer to its intended axis than the others.
        var rng = new Random(0);
        var groupA = Enumerable.Range(0, 30).Select(_ => Noisy(UnitVector(0, 8), rng)).ToList();
        var groupB = Enumerable.Range(0, 30).Select(_ => Noisy(UnitVector(3, 8), rng)).ToList();
        var groupC = Enumerable.Range(0, 30).Select(_ => Noisy(UnitVector(6, 8), rng)).ToList();
        var all = groupA.Concat(groupB).Concat(groupC).ToArray();

        var k = new KMeansClusterer(3, maxIterations: 30, seed: 42);
        var result = k.Cluster(all);

        result.Centroids.Length.Should().Be(3);
        result.Assignments.Length.Should().Be(90);

        // Group homogeneity: every member of a group lands in the same cluster.
        result.Assignments[0..30].Distinct().Should().ContainSingle("group A all in one cluster");
        result.Assignments[30..60].Distinct().Should().ContainSingle("group B all in one cluster");
        result.Assignments[60..90].Distinct().Should().ContainSingle("group C all in one cluster");

        // Different groups go to different clusters.
        var clusterOfA = result.Assignments[0];
        var clusterOfB = result.Assignments[30];
        var clusterOfC = result.Assignments[60];
        new[] { clusterOfA, clusterOfB, clusterOfC }.Distinct().Count().Should().Be(3);
    }

    [Fact]
    public void Cluster_CentroidsAreUnitNormalized()
    {
        var rng = new Random(0);
        var vectors = Enumerable.Range(0, 50)
            .Select(i => Noisy(UnitVector(i % 3 * 2, 8), rng))
            .ToArray();

        var k = new KMeansClusterer(3, seed: 42);
        var result = k.Cluster(vectors);

        foreach (var centroid in result.Centroids)
        {
            float ssq = 0;
            foreach (var x in centroid) ssq += x * x;
            MathF.Sqrt(ssq).Should().BeApproximately(1.0f, 1e-3f);
        }
    }

    [Fact]
    public void Cluster_DeterministicWithFixedSeed()
    {
        var rng = new Random(0);
        var vectors = Enumerable.Range(0, 40)
            .Select(_ => Noisy(UnitVector(0, 8), rng))
            .ToArray();

        var k1 = new KMeansClusterer(4, seed: 42).Cluster(vectors);
        var k2 = new KMeansClusterer(4, seed: 42).Cluster(vectors);

        k1.Assignments.Should().Equal(k2.Assignments);
    }

    [Fact]
    public void Cluster_ReportsProgressPerIteration()
    {
        var rng = new Random(0);
        var vectors = Enumerable.Range(0, 60)
            .Select(i => Noisy(UnitVector((i % 3) * 2, 8), rng))
            .ToArray();

        var reports = new List<KMeansProgress>();
        var progress = new Progress<KMeansProgress>(reports.Add);
        var k = new KMeansClusterer(3, maxIterations: 30, seed: 42);
        k.Cluster(vectors, progress);

        // Allow Progress<T> to flush
        Thread.Sleep(50);

        reports.Should().NotBeEmpty();
        reports[0].Iteration.Should().Be(1);
        reports[^1].Reassignments.Should().Be(0, "K-means should converge before maxIterations on this trivially separable data");
    }

    private static float[] UnitVector(int axis, int dim)
    {
        var v = new float[dim];
        v[axis] = 1f;
        return v;
    }

    private static float[] Noisy(float[] v, Random rng)
    {
        var result = new float[v.Length];
        float ssq = 0;
        for (int i = 0; i < v.Length; i++)
        {
            result[i] = v[i] + (float)((rng.NextDouble() - 0.5) * 0.1);
            ssq += result[i] * result[i];
        }
        var norm = MathF.Sqrt(ssq);
        if (norm > 0) for (int i = 0; i < v.Length; i++) result[i] /= norm;
        return result;
    }
}
