using Epub.Search;
using Epub.Search.Embedding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Epub_App.Pages;

public sealed partial class NeighborsDialog : ContentDialog
{
    private const int CandidatePool = 30;
    private const int Display = 15;

    private readonly IEmbeddingStore _store;
    private readonly MiniLmEmbedder _embedder;
    private readonly string _sourceText;
    private readonly string _sourceBookPath;
    private readonly int _sourceSpineIdx;

    public SearchHit? SelectedHit { get; private set; }

    public NeighborsDialog(string sourceText, string sourceBookPath, int sourceSpineIdx)
    {
        InitializeComponent();
        var services = App.Current.Services;
        _store = services.GetRequiredService<IEmbeddingStore>();
        _embedder = services.GetRequiredService<MiniLmEmbedder>();
        _sourceText = sourceText;
        _sourceBookPath = sourceBookPath;
        _sourceSpineIdx = sourceSpineIdx;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _embedder.EnsureReadyAsync();
            var queryEmbedding = await _embedder.EmbedAsync(new[] { _sourceText });
            var hits = await _store.SearchAsync(queryEmbedding[0], CandidatePool);

            var items = hits
                .Where(h => !(string.Equals(h.BookPath, _sourceBookPath, StringComparison.OrdinalIgnoreCase)
                              && h.SpineIdx == _sourceSpineIdx))
                .Take(Display)
                .Select(h => new NeighborItem(h))
                .ToList();

            ResultsList.ItemsSource = items;
            ProgressRing.IsActive = false;
            StatusLabel.Text = items.Count == 0
                ? "No related passages found in your library."
                : $"{items.Count} related passages from elsewhere in your library";
        }
        catch (Exception ex)
        {
            ProgressRing.IsActive = false;
            StatusLabel.Text = $"Search failed: {ex.Message}";
        }
    }

    private void Result_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NeighborItem item)
        {
            SelectedHit = item.Hit;
            Hide();
        }
    }
}

public sealed class NeighborItem
{
    public SearchHit Hit { get; }
    public string Title { get; }
    public string Snippet { get; }
    public string Subtitle { get; }

    public NeighborItem(SearchHit hit)
    {
        Hit = hit;
        Title = Path.GetFileNameWithoutExtension(hit.BookPath);
        Snippet = hit.Snippet;
        Subtitle = $"Chapter {hit.SpineIdx + 1} · similarity {hit.Similarity:F2}";
    }
}
