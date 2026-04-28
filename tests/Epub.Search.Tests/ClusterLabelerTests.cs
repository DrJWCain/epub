using Epub.Search.Clustering;
using FluentAssertions;

namespace Epub.Search.Tests;

public sealed class ClusterLabelerTests
{
    [Fact]
    public void ComputeLabels_TwoClusters_PicksDistinctiveTermsForEach()
    {
        var labeler = new ClusterLabeler(topN: 3);

        var labels = labeler.ComputeLabels(new[]
        {
            (1L, "neural networks learn weights through gradient descent backpropagation"),
            (1L, "training data feeds the network and gradients update the weights"),
            (1L, "deep learning models stack many layers of neurons"),
            (2L, "the railway system connects cities across the country"),
            (2L, "trains travel on tracks pulling passengers and freight"),
            (2L, "steam locomotives shaped industrial transport for two centuries"),
        });

        labels.Should().ContainKey(1L).And.ContainKey(2L);
        labels[1L].Should().NotBeEmpty();
        labels[2L].Should().NotBeEmpty();

        // Cluster 1 should have neural-net-flavoured terms surface; cluster 2
        // should have railway-flavoured terms. The exact ordering depends on
        // c-TF-IDF math, but the two label strings should be disjoint.
        var terms1 = labels[1L].Split(" · ").ToHashSet();
        var terms2 = labels[2L].Split(" · ").ToHashSet();
        terms1.Should().NotIntersectWith(terms2,
            "the whole point of c-TF-IDF is to surface terms that distinguish each cluster");

        var expected1 = new[] { "neural", "networks", "weights", "network", "layers", "gradient", "backpropagation", "training", "neurons", "learning", "deep" };
        var expected2 = new[] { "railway", "trains", "tracks", "locomotives", "passengers", "freight", "transport", "industrial", "stations", "system" };
        terms1.Should().IntersectWith(expected1);
        terms2.Should().IntersectWith(expected2);
    }

    [Fact]
    public void ComputeLabels_StopwordsAndShortTokens_AreIgnored()
    {
        var labeler = new ClusterLabeler(topN: 3, minTokenLength: 3);

        var labels = labeler.ComputeLabels(new[]
        {
            (1L, "the cat sat on the mat in the room and the cat was happy"),
            (2L, "all the trains run on time across the network of stations"),
        });

        var allTerms = labels.SelectMany(l => l.Value.Split(" · ")).ToHashSet();
        allTerms.Should().NotContain(new[] { "the", "and", "was", "all", "on", "in" });
        allTerms.Should().NotContain(t => t.Length < 3);
    }

    [Fact]
    public void ComputeLabels_EmptyInput_ReturnsEmpty()
    {
        var labeler = new ClusterLabeler();
        var labels = labeler.ComputeLabels(Array.Empty<(long, string)>());
        labels.Should().BeEmpty();
    }

    [Fact]
    public void ComputeLabels_SingleCluster_StillProducesNonEmptyLabel()
    {
        // With only one cluster the smoothed IDF degenerates to a constant,
        // so labels reduce to top-TF terms. Still useful, never crashes.
        var labeler = new ClusterLabeler();
        var labels = labeler.ComputeLabels(new[]
        {
            (1L, "neural networks neural networks neural learning gradient descent"),
        });

        labels.Should().ContainKey(1L);
        labels[1L].Should().NotBeNullOrWhiteSpace();
        labels[1L].Should().Contain("neural", "neural appears most often so it should surface");
    }
}
