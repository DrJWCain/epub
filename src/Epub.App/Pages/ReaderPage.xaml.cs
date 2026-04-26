using Epub.Core;
using Epub.Core.Models;
using Epub_App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Epub_App.Pages;

public sealed partial class ReaderPage : Page
{
    private readonly IBookSession _session;

    public ReaderPage()
    {
        InitializeComponent();
        _session = App.Current.Services.GetRequiredService<IBookSession>();
        ReaderControl.PageChanged += (s, e) => UpdateNavState();
        ReaderControl.SpineChanged += (s, e) => UpdateNavState();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var path = _session.PendingBookPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            _session.PendingBookPath = null; // consume
            await OpenAsync(path);
        }
    }

    private async Task OpenAsync(string path)
    {
        var reader = await EpubReader.OpenAsync(path);
        EmptyState.Visibility = Visibility.Collapsed;
        ReaderControl.Visibility = Visibility.Visible;
        await ReaderControl.LoadBookAsync(reader);
        LoadToc(reader.Book);
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
