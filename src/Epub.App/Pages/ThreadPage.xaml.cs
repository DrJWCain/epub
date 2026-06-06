using Epub.Search;
using Epub.Search.Embedding;
using Epub.Search.Llm;
using Epub_App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Epub_App.Pages;

public sealed partial class ThreadPage : Page
{
    private readonly ConceptThreadService _threadService;
    private readonly Phi4ModelDownloader _llmDownloader;
    private readonly Phi4Generator _llm;
    private readonly IEmbeddingStore _store;
    private readonly IBookSession _session;
    private CancellationTokenSource? _generateCts;

    public ThreadPage()
    {
        InitializeComponent();
        var services = App.Current.Services;
        _threadService = services.GetRequiredService<ConceptThreadService>();
        _llmDownloader = services.GetRequiredService<Phi4ModelDownloader>();
        _llm = services.GetRequiredService<Phi4Generator>();
        _store = services.GetRequiredService<IEmbeddingStore>();
        _session = services.GetRequiredService<IBookSession>();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await RefreshReadinessAsync();
    }

    private async Task RefreshReadinessAsync()
    {
        var meta = await Task.Run(() => _store.GetMetaAsync()).ConfigureAwait(true);
        if (meta.ChunkCount == 0)
        {
            QueryBox.IsEnabled = false;
            GenerateButton.IsEnabled = false;
            ShowEmpty("Index your library in Settings first — Threads needs a populated semantic index to work.");
            return;
        }

        if (!_llmDownloader.IsDownloaded)
        {
            QueryBox.IsEnabled = false;
            GenerateButton.Content = "Download AI model";
            GenerateButton.IsEnabled = true;
            ShowEmpty(
                "Generating threads needs a one-time ~4.86 GB AI model download " +
                "(Phi-4-mini-instruct CPU INT4). Click “Download AI model” to begin. " +
                "After download everything runs locally; nothing leaves your machine.");
            return;
        }

        QueryBox.IsEnabled = true;
        GenerateButton.Content = "Generate";
        GenerateButton.IsEnabled = true;
        if (ThreadPanel.Children.Count == 0)
            ShowEmpty($"Type a question or concept above to generate a reading sequence drawn from {meta.IndexedBookCount} books in your library.");
    }

    private void ShowEmpty(string message)
    {
        EmptyState.Text = message;
        EmptyState.Visibility = Visibility.Visible;
        ThreadPanel.Children.Clear();
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (!_llmDownloader.IsDownloaded)
        {
            await DownloadModelAsync();
            return;
        }

        var query = QueryBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;
        var length = LengthBox.SelectedItem is int n ? n : 10;

        _generateCts = new CancellationTokenSource();
        SetWorkingState(true, "Embedding query…");
        ThreadPanel.Children.Clear();
        EmptyState.Visibility = Visibility.Collapsed;

        var progress = new Progress<ThreadProgress>(p => StatusLabel.Text = p.Phase);

        // Estimate the LLM's output length so we can drive a determinate
        // progress bar + give the user a sense of "how much further?"
        // Each step is ~30 tokens (id + transition + JSON punctuation),
        // plus ~50 tokens of wrapping JSON structure.
        var estimatedTotalTokens = length * 30 + 50;
        int tokenCount = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastUiTick = System.Diagnostics.Stopwatch.StartNew();
        bool generationStarted = false;

        Action<string> onToken = _ =>
        {
            tokenCount++;
            // Throttle UI updates to ~4/sec — token rate is 5-15/sec on ARM64
            // CPU, but updating ProgressBar + TextBlock per token still adds
            // up enough to be worth batching.
            if (lastUiTick.ElapsedMilliseconds < 250) return;
            lastUiTick.Restart();
            int captured = tokenCount;
            double elapsed = sw.Elapsed.TotalSeconds;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!generationStarted)
                {
                    generationStarted = true;
                    ProgressBar.IsIndeterminate = false;
                    ProgressBar.Maximum = estimatedTotalTokens;
                }
                ProgressBar.Value = Math.Min(captured, estimatedTotalTokens);
                var tps = captured / Math.Max(elapsed, 0.1);
                var remainingSec = Math.Max(0, (estimatedTotalTokens - captured) / Math.Max(tps, 0.1));
                StatusLabel.Text = remainingSec > 1
                    ? $"Generating thread… {captured} tokens · {tps:F1}/s · ~{FormatRemaining(remainingSec)} remaining"
                    : $"Generating thread… {captured} tokens · {tps:F1}/s · finishing up";
            });
        };

        try
        {
            var thread = await _threadService.GenerateAsync(
                query, length, progress, onToken, _generateCts.Token);

            if (thread.Steps.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ThreadPage] No usable thread for query \"{query}\" (length {length}, " +
                    $"{tokenCount} tokens, raw output {thread.RawLlmOutput.Length} chars). Raw output follows:");
                System.Diagnostics.Debug.WriteLine(thread.RawLlmOutput);
                System.Diagnostics.Debug.WriteLine("[ThreadPage] -- end raw output --");
                StatusLabel.Text =
                    $"The model returned no usable thread (output {tokenCount} tokens). Try a different query or rebuild the index.";
            }
            else
            {
                RenderThread(thread);
                // Auto-save successful threads — they're expensive to regenerate
                // and the Saved menu has a delete action for any the user doesn't
                // want to keep.
                try { await _threadService.SaveAsync(thread, _generateCts.Token); }
                catch (Exception saveEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[ThreadPage] save failed: {saveEx}");
                }
                StatusLabel.Text = $"{thread.Steps.Count} passages · {tokenCount} tokens generated · saved";
            }
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Generate failed: {ex.Message}";
        }
        finally
        {
            _generateCts?.Dispose();
            _generateCts = null;
            SetWorkingState(false);
        }
    }

    private async Task DownloadModelAsync()
    {
        _generateCts = new CancellationTokenSource();
        SetWorkingState(true, "Preparing download…");
        ProgressBar.IsIndeterminate = false;

        var progress = new Progress<DownloadProgress>(p =>
        {
            ProgressBar.Maximum = Math.Max(1, p.TotalBytes);
            ProgressBar.Value = p.BytesDone;
            StatusLabel.Text =
                $"Downloading {p.FileName}: {FormatBytes(p.BytesDone)} of {FormatBytes(p.TotalBytes)}";
        });

        try
        {
            await _llm.EnsureReadyAsync(progress, _generateCts.Token);
            StatusLabel.Text = "AI model ready.";
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            _generateCts?.Dispose();
            _generateCts = null;
            ProgressBar.IsIndeterminate = true;
            SetWorkingState(false);
            await RefreshReadinessAsync();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _generateCts?.Cancel();

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && GenerateButton.IsEnabled)
        {
            Generate_Click(sender, e);
            e.Handled = true;
        }
    }

    private void SetWorkingState(bool working, string? statusText = null)
    {
        GenerateButton.Visibility = working ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        QueryBox.IsEnabled = !working;
        LengthBox.IsEnabled = !working;
        if (statusText is not null) StatusLabel.Text = statusText;
    }

    private void RenderThread(ConceptThread thread)
    {
        ThreadPanel.Children.Clear();
        EmptyState.Visibility = Visibility.Collapsed;
        for (int i = 0; i < thread.Steps.Count; i++)
        {
            var step = thread.Steps[i];
            if (!string.IsNullOrWhiteSpace(step.Transition))
            {
                ThreadPanel.Children.Add(new TextBlock
                {
                    Text = step.Transition,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextWrapping = TextWrapping.WrapWholeWords,
                    Margin = new Thickness(8, 8, 8, 0),
                });
            }
            ThreadPanel.Children.Add(BuildPassageCard(step.Passage));
        }
    }

    private Border BuildPassageCard(SearchHit hit)
    {
        var card = new Border
        {
            Padding = new Thickness(16),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(hit.BookPath),
            FontWeight = FontWeights.SemiBold,
        });
        stack.Children.Add(new TextBlock
        {
            Text = hit.Snippet,
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"Chapter {hit.SpineIdx + 1}  →  open in reader",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        card.Child = stack;

        card.PointerEntered += (_, _) => card.Opacity = 0.85;
        card.PointerExited += (_, _) => card.Opacity = 1.0;
        card.PointerPressed += (_, _) =>
        {
            if (!File.Exists(hit.BookPath)) return;
            _session.PendingBookPath = hit.BookPath;
            _session.PendingSpineIndex = hit.SpineIdx;
            _session.PendingProbeText = hit.Snippet;
            (App.Current.MainWindow as MainWindow)?.NavigateToReaderTab();
        };
        return card;
    }

    private async void SavedFlyout_Opening(object? sender, object e)
    {
        var saved = await Task.Run(() => _threadService.ListSavedAsync());
        var items = saved.Select(s => new SavedThreadItem(s)).ToList();
        SavedList.ItemsSource = items;
        SavedEmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void SavedList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SavedThreadItem item) return;
        SavedFlyout.Hide();

        SetWorkingState(true, $"Loading saved thread: {item.Query}…");
        try
        {
            var thread = await Task.Run(() => _threadService.LoadSavedAsync(item.Id));
            if (thread is null)
            {
                StatusLabel.Text = "Saved thread could not be loaded (data missing or corrupted).";
                return;
            }
            QueryBox.Text = thread.Query;
            RenderThread(thread);
            StatusLabel.Text = $"Loaded saved thread · {thread.Steps.Count} passages";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Load failed: {ex.Message}";
        }
        finally
        {
            SetWorkingState(false);
        }
    }

    private async void DeleteSaved_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not long id) return;
        await Task.Run(() => _threadService.DeleteSavedAsync(id));
        // Refresh the list within the open flyout
        var saved = await Task.Run(() => _threadService.ListSavedAsync());
        var items = saved.Select(s => new SavedThreadItem(s)).ToList();
        SavedList.ItemsSource = items;
        SavedEmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SavedList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string FormatRemaining(double seconds)
    {
        if (seconds < 60) return $"{seconds:F0}s";
        var min = (int)(seconds / 60);
        var sec = (int)(seconds % 60);
        return $"{min}m {sec:D2}s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public sealed class SavedThreadItem
{
    public long Id { get; }
    public string Query { get; }
    public string CreatedDisplay { get; }

    public SavedThreadItem(SavedThreadSummary summary)
    {
        Id = summary.Id;
        Query = summary.Query;
        CreatedDisplay = summary.CreatedAt.LocalDateTime.ToString("f");
    }
}
