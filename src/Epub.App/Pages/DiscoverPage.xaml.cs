using Epub.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Epub_App.Pages;

public sealed partial class DiscoverPage : Page
{
    private readonly IEmbeddingStore _store;

    public DiscoverPage()
    {
        InitializeComponent();
        _store = App.Current.Services.GetRequiredService<IEmbeddingStore>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var meta = await Task.Run(() => _store.GetMetaAsync()).ConfigureAwait(true);
        var clusters = await Task.Run(() => _store.GetClustersAsync()).ConfigureAwait(true);

        if (meta.ChunkCount == 0)
        {
            ShowEmpty("Index your library in Settings first, then build themes to populate this page.");
            StatusLabel.Text = "";
            return;
        }
        if (clusters.Count == 0)
        {
            ShowEmpty("No themes built yet. Open Settings → Themes → Build themes.");
            StatusLabel.Text = "";
            return;
        }

        var cards = clusters
            .Select(c => new ClusterCard(c))
            .ToList();
        ClusterGrid.ItemsSource = cards;
        ClusterGrid.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;

        var built = clusters.Max(c => c.BuiltAt);
        var totalChunks = clusters.Sum(c => c.ChunkCount);
        StatusLabel.Text =
            $"{clusters.Count} themes · {totalChunks:N0} passages · built {built.LocalDateTime:f}";
    }

    private void ShowEmpty(string message)
    {
        EmptyState.Text = message;
        EmptyState.Visibility = Visibility.Visible;
        ClusterGrid.Visibility = Visibility.Collapsed;
        ClusterGrid.ItemsSource = null;
    }

    private void Cluster_Click(object sender, ItemClickEventArgs e)
    {
        // S3.3 wires the detail view. For now, no-op — visible indication is
        // just the card's hover state.
        _ = e;
    }
}

public sealed class ClusterCard
{
    public ClusterRow Row { get; }
    public string Title { get; }
    public string Subtitle { get; }

    public ClusterCard(ClusterRow row)
    {
        Row = row;
        Title = string.IsNullOrWhiteSpace(row.Label) ? $"Theme #{row.Id}" : row.Label!;
        Subtitle = row.BookCount > 0
            ? $"{row.ChunkCount:N0} passages · {row.BookCount} {(row.BookCount == 1 ? "book" : "books")}"
            : $"{row.ChunkCount:N0} passages";
    }
}
