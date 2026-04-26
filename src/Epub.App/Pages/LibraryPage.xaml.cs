using Epub.Library;
using Epub_App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace Epub_App.Pages;

public sealed partial class LibraryPage : Page
{
    private readonly ILibraryService _library;
    private readonly ISettingsService _settings;
    private readonly IBookSession _session;

    public LibraryPage()
    {
        InitializeComponent();
        _library = App.Current.Services.GetRequiredService<ILibraryService>();
        _settings = App.Current.Services.GetRequiredService<ISettingsService>();
        _session = App.Current.Services.GetRequiredService<IBookSession>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await LoadLibraryAsync();
    }

    private async Task LoadLibraryAsync()
    {
        var path = _settings.LibraryFolderPath;
        if (string.IsNullOrEmpty(path))
        {
            ShowEmptyState("No library folder configured.\nOpen Settings (gear icon) and pick the folder containing your EPUBs.");
            StatusLabel.Text = "";
            return;
        }
        if (!Directory.Exists(path))
        {
            ShowEmptyState($"Library folder not found:\n{path}");
            StatusLabel.Text = "";
            return;
        }

        StatusLabel.Text = "Loading…";
        BookGrid.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Collapsed;

        var entries = await _library.ScanFolderAsync(path);

        var items = new List<LibraryGridItem>(entries.Count);
        foreach (var entry in entries)
            items.Add(await LibraryGridItem.CreateAsync(entry));

        BookGrid.ItemsSource = items;
        if (items.Count == 0)
        {
            ShowEmptyState($"No .epub files in:\n{path}");
            StatusLabel.Text = "";
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
            BookGrid.Visibility = Visibility.Visible;
            StatusLabel.Text = $"{items.Count} book{(items.Count == 1 ? "" : "s")}";
        }
    }

    private void ShowEmptyState(string message)
    {
        EmptyState.Text = message;
        EmptyState.Visibility = Visibility.Visible;
        BookGrid.Visibility = Visibility.Collapsed;
        BookGrid.ItemsSource = null;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadLibraryAsync();

    private void Book_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LibraryGridItem item)
        {
            _session.PendingBookPath = item.FilePath;
            (App.Current.MainWindow as MainWindow)?.NavigateToReaderTab();
        }
    }
}

public sealed class LibraryGridItem
{
    public string FilePath { get; }
    public string Title { get; }
    public string Authors { get; }
    public BitmapImage? Cover { get; }

    private LibraryGridItem(string filePath, string title, string authors, BitmapImage? cover)
    {
        FilePath = filePath;
        Title = title;
        Authors = authors;
        Cover = cover;
    }

    public static async Task<LibraryGridItem> CreateAsync(LibraryEntry entry)
    {
        BitmapImage? cover = null;
        if (entry.CoverImageBytes is { Length: > 0 })
        {
            cover = new BitmapImage { DecodePixelHeight = 220 };
            using var ms = new MemoryStream(entry.CoverImageBytes);
            await cover.SetSourceAsync(ms.AsRandomAccessStream());
        }
        return new LibraryGridItem(
            entry.FilePath,
            entry.Title,
            string.Join(", ", entry.Authors),
            cover);
    }
}
