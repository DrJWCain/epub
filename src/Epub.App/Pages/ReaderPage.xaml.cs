using Epub.Core;
using Epub.Core.Models;
using Epub.Library;
using Epub_App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Epub_App.Pages;

public sealed partial class ReaderPage : Page
{
    private readonly IBookSession _session;
    private readonly IPositionStore _positionStore;
    private readonly DispatcherQueueTimer _saveTimer;
    private string? _currentBookTitle;
    private string? _currentBookPath;

    public ReaderPage()
    {
        InitializeComponent();
        _session = App.Current.Services.GetRequiredService<IBookSession>();
        _positionStore = App.Current.Services.GetRequiredService<IPositionStore>();

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromSeconds(1);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (s, e) => _ = SavePositionAsync();

        ReaderControl.PageChanged += (s, e) => { UpdateNavState(); SchedulePositionSave(); };
        ReaderControl.SpineChanged += (s, e) => UpdateNavState();
        ReaderControl.NeighborsRequested += OnNeighborsRequested;
    }

    private async void OnNeighborsRequested(object? sender, string sourceText)
    {
        if (string.IsNullOrEmpty(_currentBookPath) || ReaderControl.CurrentSpineIndex < 0) return;

        var dialog = new NeighborsDialog(sourceText, _currentBookPath, ReaderControl.CurrentSpineIndex)
        {
            XamlRoot = this.XamlRoot,
        };
        await dialog.ShowAsync();

        if (dialog.SelectedHit is { } hit && File.Exists(hit.BookPath))
        {
            await OpenAsync(hit.BookPath, hit.SpineIdx, hit.Snippet);
        }
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_currentBookTitle is not null)
            (App.Current.MainWindow as MainWindow)?.SetTitleBarTitle(_currentBookTitle);

        var path = _session.PendingBookPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            // Snapshot every pending field together so a slow Open doesn't race a new
            // search-result navigation that arrives mid-flight.
            var pendingSpine = _session.PendingSpineIndex;
            var pendingProbe = _session.PendingProbeText;
            _session.PendingBookPath = null;
            _session.PendingSpineIndex = null;
            _session.PendingProbeText = null;
            await OpenAsync(path, pendingSpine, pendingProbe);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        (App.Current.MainWindow as MainWindow)?.SetTitleBarTitle(null);
    }

    private async Task OpenAsync(string path, int? pendingSpineIndex = null, string? pendingProbeText = null)
    {
        var reader = await EpubReader.OpenAsync(path);

        // Once the parser accepts the file, claim it so any incoming page-change
        // events save against the right path. Set before LoadBookAsync triggers
        // the first PageChanged.
        _currentBookPath = path;

        EmptyState.Visibility = Visibility.Collapsed;
        ReaderControl.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(pendingProbeText))
        {
            // Search-result hand-off — land at the matching passage rather than
            // the last-read position. The position store still gets updated as
            // soon as the user turns a page (see SchedulePositionSave).
            await ReaderControl.LoadBookAtTextAsync(
                reader,
                initialSpineIndex: pendingSpineIndex ?? 0,
                probeText: pendingProbeText);
        }
        else
        {
            // Microsoft.Data.Sqlite's "async" methods run synchronously, so a
            // direct await on the UI thread blocks it for the full busy_timeout
            // window if the indexer currently holds the write lock. Hop to the
            // thread pool.
            var saved = await Task.Run(() => _positionStore.GetAsync(path));
            await ReaderControl.LoadBookAsync(
                reader,
                initialSpineIndex: saved?.SpineIndex ?? 0,
                initialPageInChapter: saved?.PageInChapter ?? 0);
        }

        LoadToc(reader.Book);
        _currentBookTitle = reader.Book.Metadata.Title;
        (App.Current.MainWindow as MainWindow)?.SetTitleBarTitle(_currentBookTitle);
    }

    private void SchedulePositionSave()
    {
        if (_currentBookPath is null) return;
        if (ReaderControl.CurrentSpineIndex < 0) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SavePositionAsync()
    {
        var path = _currentBookPath;
        if (path is null) return;
        if (ReaderControl.CurrentSpineIndex < 0) return;

        var position = new ReadingPosition(
            ReaderControl.CurrentSpineIndex,
            ReaderControl.CurrentPageInChapter,
            DateTimeOffset.UtcNow);
        try
        {
            // Microsoft.Data.Sqlite is synchronous-under-the-hood, so a save
            // contending with the indexer's write lock would otherwise block
            // the UI thread for up to 30 s (busy_timeout). Hop off the UI.
            await Task.Run(() => _positionStore.SaveAsync(path, position));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReaderPage] SavePositionAsync failed: {ex}");
        }
    }

    private void LoadToc(Book book)
    {
        var items = new List<TocItem>();
        if (book.Toc is not null)
            FlattenToc(book.Toc.Nodes, depth: 0, items);
        TocList.ItemsSource = items;
        TocToggle.IsEnabled = items.Count > 0;
    }

    private static void FlattenToc(IReadOnlyList<TocNode> nodes, int depth, List<TocItem> output)
    {
        foreach (var node in nodes)
        {
            output.Add(new TocItem(node.Title, node.Href, depth));
            if (node.Children.Count > 0)
                FlattenToc(node.Children, depth + 1, output);
        }
    }

    private void UpdateNavState()
    {
        PrevButton.IsEnabled = ReaderControl.CanGoBack;
        NextButton.IsEnabled = ReaderControl.CanGoForward;
        ProgressLabel.Text = ReaderControl.SpineCount > 0
            ? $"Ch {ReaderControl.CurrentSpineIndex + 1} / {ReaderControl.SpineCount}  •  Page {ReaderControl.CurrentPageInChapter + 1} / {ReaderControl.ChapterPageCount}"
            : "—";
    }

    private async void OpenEpub_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".epub");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await OpenAsync(file.Path);
    }

    private void TocToggle_Click(object sender, RoutedEventArgs e)
    {
        TocSplitView.IsPaneOpen = TocToggle.IsChecked == true;
    }

    private void TocList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TocItem item && !string.IsNullOrEmpty(item.Href))
        {
            ReaderControl.TryNavigateToHref(item.Href);
            TocSplitView.IsPaneOpen = false;
            TocToggle.IsChecked = false;
        }
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) => await ReaderControl.GoBackAsync();

    private async void Next_Click(object sender, RoutedEventArgs e) => await ReaderControl.GoForwardAsync();
}

public sealed class TocItem
{
    public string Title { get; }
    public string? Href { get; }
    public int Depth { get; }
    public Thickness PaddingLeft { get; }

    public TocItem(string title, string? href, int depth)
    {
        Title = title;
        Href = href;
        Depth = depth;
        PaddingLeft = new Thickness(Math.Min(depth, 4) * 16, 4, 8, 4);
    }
}
