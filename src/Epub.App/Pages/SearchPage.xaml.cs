using Epub.Search;
using Epub.Search.Embedding;
using Epub_App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Epub_App.Pages;

public sealed partial class SearchPage : Page
{
    private const int ResultLimit = 25;
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(250);

    private readonly IEmbeddingStore _store;
    private readonly MiniLmEmbedder _embedder;
    private readonly MiniLmModelDownloader _downloader;
    private readonly IBookSession _session;
    private CancellationTokenSource? _queryCts;

    public SearchPage()
    {
        InitializeComponent();
        var services = App.Current.Services;
        _store = services.GetRequiredService<IEmbeddingStore>();
        _embedder = services.GetRequiredService<MiniLmEmbedder>();
        _downloader = services.GetRequiredService<MiniLmModelDownloader>();
        _session = services.GetRequiredService<IBookSession>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RefreshReadinessAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _queryCts?.Cancel();
    }

    private async Task RefreshReadinessAsync()
    {
        var meta = await _store.GetMetaAsync();
        if (!_downloader.IsDownloaded || meta.IndexedBookCount == 0)
        {
            QueryBox.IsEnabled = false;
            ResultsList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            EmptyState.Text = !_downloader.IsDownloaded
                ? "Search needs the AI model. Open Settings and click Build index — the model downloads once and the build runs offline after that."
                : "No books indexed yet. Open Settings and click Build index.";
        }
        else
        {
            QueryBox.IsEnabled = true;
            EmptyState.Visibility = string.IsNullOrWhiteSpace(QueryBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (string.IsNullOrWhiteSpace(QueryBox.Text))
            {
                EmptyState.Text =
                    $"Type a query to search {meta.IndexedBookCount} indexed book{(meta.IndexedBookCount == 1 ? "" : "s")} ({meta.ChunkCount:N0} passages).";
                ResultsList.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        _queryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _queryCts = cts;
        var ct = cts.Token;
        var text = QueryBox.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            ResultsList.ItemsSource = null;
            ResultsList.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            StatusLabel.Text = "";
            await RefreshReadinessAsync();
            return;
        }

        try
        {
            await Task.Delay(DebounceInterval, ct);
            StatusLabel.Text = "Searching…";
            await _embedder.EnsureReadyAsync(progress: null, ct);

            var queryEmbedding = await _embedder.EmbedAsync(new[] { text }, ct);
            var hits = await _store.SearchAsync(queryEmbedding[0], ResultLimit, ct);

            if (ct.IsCancellationRequested) return;

            var items = hits.Select(h => new SearchResultItem(h)).ToList();
            ResultsList.ItemsSource = items;
            ResultsList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Text = items.Count > 0 ? "" : "No matching passages.";
            StatusLabel.Text = items.Count > 0 ? $"{items.Count} matches" : "";
        }
        catch (OperationCanceledException) { /* expected when query changes */ }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Search failed: {ex.Message}";
        }
    }

    private void Result_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResultItem item) return;
        if (!File.Exists(item.BookPath))
        {
            StatusLabel.Text = $"Book file no longer at {item.BookPath}";
            return;
        }

        _session.PendingBookPath = item.BookPath;
        _session.PendingSpineIndex = item.SpineIdx;
        _session.PendingProbeText = item.ProbeText;
        (App.Current.MainWindow as MainWindow)?.NavigateToReaderTab();
    }
}

public sealed class SearchResultItem
{
    public string Title { get; }
    public string Snippet { get; }
    public string Subtitle { get; }
    public string BookPath { get; }
    public int SpineIdx { get; }
    public string ProbeText { get; }

    public SearchResultItem(SearchHit hit)
    {
        Title = Path.GetFileNameWithoutExtension(hit.BookPath);
        Snippet = hit.Snippet;
        Subtitle = $"Chapter {hit.SpineIdx + 1} · similarity {hit.Similarity:F2}";
        BookPath = hit.BookPath;
        SpineIdx = hit.SpineIdx;
        ProbeText = hit.Snippet;
    }
}
