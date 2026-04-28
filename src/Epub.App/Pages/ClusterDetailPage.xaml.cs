using Epub.Search;
using Epub_App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Epub_App.Pages;

public sealed partial class ClusterDetailPage : Page
{
    private readonly IEmbeddingStore _store;
    private readonly IBookSession _session;
    private ClusterRow? _cluster;

    public ClusterDetailPage()
    {
        InitializeComponent();
        var services = App.Current.Services;
        _store = services.GetRequiredService<IEmbeddingStore>();
        _session = services.GetRequiredService<IBookSession>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not ClusterRow row) return;
        _cluster = row;

        TitleText.Text = string.IsNullOrWhiteSpace(row.Label) ? $"Theme #{row.Id}" : row.Label!;
        StatusLabel.Text =
            $"{row.ChunkCount:N0} passages · {row.BookCount} {(row.BookCount == 1 ? "book" : "books")}";

        var passages = await Task.Run(() => _store.GetClusterPassagesAsync(row.Id));

        var grouped = passages
            .GroupBy(p => Path.GetFileNameWithoutExtension(p.BookPath))
            .Select(g => new PassageGroup(g.Key, g.Select(p => new PassageItem(p))))
            .ToList();
        GroupedPassages.Source = grouped;
    }

    private void Passage_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PassageItem item) return;
        var p = item.Passage;
        if (!File.Exists(p.BookPath))
        {
            StatusLabel.Text = $"Book file no longer at {p.BookPath}";
            return;
        }

        _session.PendingBookPath = p.BookPath;
        _session.PendingSpineIndex = p.SpineIdx;
        _session.PendingProbeText = p.Snippet;
        (App.Current.MainWindow as MainWindow)?.NavigateToReaderTab();
    }
}

public sealed class PassageGroup : List<PassageItem>
{
    public string Key { get; }
    public IEnumerable<PassageItem> Items => this;

    public PassageGroup(string key, IEnumerable<PassageItem> items) : base(items)
    {
        Key = key;
    }
}

public sealed class PassageItem
{
    public ClusterPassage Passage { get; }
    public string Snippet { get; }
    public string Subtitle { get; }

    public PassageItem(ClusterPassage passage)
    {
        Passage = passage;
        Snippet = passage.Snippet;
        Subtitle = $"Chapter {passage.SpineIdx + 1}";
    }
}
