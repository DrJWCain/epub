using Epub.Search;
using Epub.Search.Clustering;
using Epub.Search.Embedding;
using Epub_App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Epub_App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly ISettingsService _settings;
    private readonly IEmbeddingStore _embeddingStore;
    private readonly IIndexingService _indexing;
    private readonly MiniLmEmbedder _embedder;
    private readonly IndexingBackgroundCoordinator _coordinator;
    private readonly IClusteringService _clustering;
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _themesCts;

    public SettingsPage()
    {
        InitializeComponent();
        var services = App.Current.Services;
        _settings = services.GetRequiredService<ISettingsService>();
        _embeddingStore = services.GetRequiredService<IEmbeddingStore>();
        _indexing = services.GetRequiredService<IIndexingService>();
        _embedder = services.GetRequiredService<MiniLmEmbedder>();
        _coordinator = services.GetRequiredService<IndexingBackgroundCoordinator>();
        _clustering = services.GetRequiredService<IClusteringService>();

        _settings.Changed += (s, e) =>
        {
            RefreshFolderRow();
            _ = RefreshIndexStatusAsync();
            _ = RefreshThemesStatusAsync();
        };
        RefreshFolderRow();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _coordinator.StatusChanged += OnCoordinatorStatusChanged;
        _ = RefreshIndexStatusAsync();
        _ = RefreshThemesStatusAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _coordinator.StatusChanged -= OnCoordinatorStatusChanged;
    }

    private void OnCoordinatorStatusChanged(object? sender, IndexingBackgroundCoordinator.CoordinatorStatus snapshot)
    {
        DispatcherQueue.TryEnqueue(async () => await RefreshIndexStatusAsync());
    }

    private void RefreshFolderRow()
    {
        var path = _settings.LibraryFolderPath;
        FolderPathText.Text = string.IsNullOrEmpty(path) ? "(not set)" : path;
        ClearFolderButton.IsEnabled = !string.IsNullOrEmpty(path);
    }

    private async Task RefreshIndexStatusAsync()
    {
        var meta = await _embeddingStore.GetMetaAsync();
        var folder = _settings.LibraryFolderPath;
        int folderCount = string.IsNullOrEmpty(folder) || !Directory.Exists(folder)
            ? 0
            : Directory.EnumerateFiles(folder, "*.epub", SearchOption.TopDirectoryOnly).Count();

        if (folderCount == 0)
        {
            IndexStatusText.Text = "Set a library folder above first.";
            BuildIndexButton.IsEnabled = false;
        }
        else
        {
            var coordinatorStatus = _coordinator.CurrentStatus;
            IndexStatusText.Text = coordinatorStatus is not null
                ? $"{coordinatorStatus} (auto) · indexed {meta.IndexedBookCount} of {folderCount} books"
                : $"Indexed {meta.IndexedBookCount} of {folderCount} books · {meta.ChunkCount:N0} chunks";
            BuildIndexButton.IsEnabled = !_indexing.IsRunning;

            // Mirror auto-coordinator state into the progress UI when there's no
            // manual Build in flight (BuildIndex_Click owns it during manual mode).
            if (_indexCts is null)
            {
                if (coordinatorStatus is not null)
                {
                    IndexProgressBar.Visibility = Visibility.Visible;
                    IndexProgressText.Visibility = Visibility.Visible;
                    IndexProgressText.Text = coordinatorStatus;
                    var progress = _coordinator.CurrentProgress;
                    if (progress is { ChunksTotal: > 0 } p)
                    {
                        IndexProgressBar.IsIndeterminate = false;
                        IndexProgressBar.Maximum = p.ChunksTotal;
                        IndexProgressBar.Value = p.ChunksDone;
                    }
                    else
                    {
                        IndexProgressBar.IsIndeterminate = true;
                        IndexProgressBar.Value = 0;
                    }
                }
                else
                {
                    IndexProgressBar.Visibility = Visibility.Collapsed;
                    IndexProgressText.Visibility = Visibility.Collapsed;
                    IndexProgressBar.IsIndeterminate = false;
                    IndexProgressBar.Value = 0;
                }
            }
        }
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        _settings.LibraryFolderPath = folder.Path;
    }

    private void ClearFolder_Click(object sender, RoutedEventArgs e)
    {
        _settings.LibraryFolderPath = null;
    }

    private async void BuildIndex_Click(object sender, RoutedEventArgs e)
    {
        var folder = _settings.LibraryFolderPath;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

        var paths = Directory
            .EnumerateFiles(folder, "*.epub", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0) return;

        _indexCts = new CancellationTokenSource();
        BuildIndexButton.Visibility = Visibility.Collapsed;
        CancelIndexButton.Visibility = Visibility.Visible;
        IndexProgressBar.Visibility = Visibility.Visible;
        IndexProgressText.Visibility = Visibility.Visible;
        IndexProgressBar.IsIndeterminate = true;
        IndexProgressText.Text = "Preparing AI model (one-time download on first build)…";

        var downloadProgress = new Progress<DownloadProgress>(p =>
        {
            if (p.TotalBytes > 0)
            {
                IndexProgressBar.IsIndeterminate = false;
                IndexProgressBar.Maximum = p.TotalBytes;
                IndexProgressBar.Value = p.BytesDone;
                IndexProgressText.Text =
                    $"Downloading {p.FileName}: {FormatBytes(p.BytesDone)} of {FormatBytes(p.TotalBytes)}";
            }
        });
        var indexProgress = new Progress<IndexProgress>(p =>
        {
            if (p.LastError is not null)
            {
                IndexProgressText.Text = $"Skipped {p.CurrentBookTitle}: {p.LastError}";
                return;
            }
            IndexProgressBar.IsIndeterminate = p.ChunksTotal == 0;
            if (p.ChunksTotal > 0)
            {
                IndexProgressBar.Maximum = p.ChunksTotal;
                IndexProgressBar.Value = p.ChunksDone;
            }
            IndexProgressText.Text = p.ChunksTotal > 0
                ? $"{p.CurrentBookTitle} (book {p.BookOrdinal + 1}/{p.BookCount}) — chunk {p.ChunksDone}/{p.ChunksTotal}"
                : $"{p.CurrentBookTitle} (book {p.BookOrdinal + 1}/{p.BookCount}) — extracting…";
        });

        try
        {
            await _embedder.EnsureReadyAsync(downloadProgress, _indexCts.Token);
            IndexProgressBar.IsIndeterminate = true;
            IndexProgressText.Text = "Starting index build…";
            await _indexing.BuildAllAsync(paths, indexProgress, _indexCts.Token);
            IndexProgressText.Text = "Index build complete.";
        }
        catch (OperationCanceledException)
        {
            IndexProgressText.Text = "Index build cancelled.";
        }
        catch (Exception ex)
        {
            IndexProgressText.Text = $"Index build failed: {ex.Message}";
        }
        finally
        {
            _indexCts.Dispose();
            _indexCts = null;
            BuildIndexButton.Visibility = Visibility.Visible;
            CancelIndexButton.Visibility = Visibility.Collapsed;
            IndexProgressBar.IsIndeterminate = false;
            await RefreshIndexStatusAsync();
        }
    }

    private void CancelIndex_Click(object sender, RoutedEventArgs e) => _indexCts?.Cancel();

    private async Task RefreshThemesStatusAsync()
    {
        var meta = await _embeddingStore.GetMetaAsync();
        var clusters = await _embeddingStore.GetClustersAsync();
        if (meta.ChunkCount == 0)
        {
            ThemesStatusText.Text = "Build the semantic index first.";
            BuildThemesButton.IsEnabled = false;
        }
        else if (clusters.Count == 0)
        {
            ThemesStatusText.Text = "Themes not built yet.";
            BuildThemesButton.IsEnabled = !_clustering.IsRunning;
        }
        else
        {
            var built = clusters.Max(c => c.BuiltAt);
            var totalChunks = clusters.Sum(c => c.ChunkCount);
            ThemesStatusText.Text =
                $"{clusters.Count} themes · {totalChunks:N0} passages · built {built.LocalDateTime:f}";
            BuildThemesButton.IsEnabled = !_clustering.IsRunning;

            // Quick-peek of the top-N labels (clusters are returned largest-first
            // by GetClustersAsync). Acts as a sanity check until the Discover
            // page (S3.2) provides a proper view.
            var top = clusters.Take(5)
                .Select(c => string.IsNullOrWhiteSpace(c.Label) ? $"#{c.Id}" : c.Label)
                .ToList();
            if (top.Count > 0)
            {
                ThemesSampleText.Text = "Top themes: " + string.Join("  •  ", top.Select(t => $"“{t}”"));
                ThemesSampleText.Visibility = Visibility.Visible;
            }
            else
            {
                ThemesSampleText.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async void BuildThemes_Click(object sender, RoutedEventArgs e)
    {
        _themesCts = new CancellationTokenSource();
        BuildThemesButton.IsEnabled = false;
        ThemesProgressBar.Visibility = Visibility.Visible;
        ThemesProgressText.Visibility = Visibility.Visible;
        ThemesProgressBar.IsIndeterminate = true;
        ThemesProgressText.Text = "Loading embeddings…";

        var progress = new Progress<ClusteringProgress>(p =>
        {
            ThemesProgressBar.IsIndeterminate = p.Total == 0;
            if (p.Total > 0)
            {
                ThemesProgressBar.Maximum = p.Total;
                ThemesProgressBar.Value = p.Current;
            }
            ThemesProgressText.Text = p.Total > 0
                ? $"{p.Phase} ({p.Current}/{p.Total})"
                : $"{p.Phase}";
        });

        try
        {
            await _clustering.RecomputeAsync(k: null, progress, _themesCts.Token);
            ThemesProgressText.Text = "Themes built.";
        }
        catch (OperationCanceledException)
        {
            ThemesProgressText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            ThemesProgressText.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            _themesCts.Dispose();
            _themesCts = null;
            ThemesProgressBar.IsIndeterminate = false;
            await RefreshThemesStatusAsync();
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
