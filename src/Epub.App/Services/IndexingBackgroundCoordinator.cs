using System.Threading.Channels;
using Epub.Library;
using Epub.Search;
using Epub.Search.Embedding;
using IndexProgress = Epub.Search.IndexProgress;

namespace Epub_App.Services;

/// <summary>
/// Listens for library scans and incrementally indexes any books that aren't yet
/// in the embedding store, on a background worker so the UI thread stays responsive.
/// Defers automatic work until the MiniLM model has been downloaded — i.e. until
/// the user has explicitly clicked Build index in Settings at least once. After that,
/// dropping a new EPUB into the library folder and re-visiting the Library page is
/// enough to get it indexed.
/// </summary>
public sealed class IndexingBackgroundCoordinator : IDisposable
{
    private readonly ILibraryService _library;
    private readonly IEmbeddingStore _store;
    private readonly IIndexingService _indexing;
    private readonly MiniLmModelDownloader _downloader;
    private readonly Channel<string> _queue;
    private readonly CancellationTokenSource _workerCts;
    private readonly Task _workerTask;

    public event EventHandler<CoordinatorStatus>? StatusChanged;

    /// <summary>Atomic snapshot of the coordinator's current status. Subscribers
    /// receive an immutable record per event so UI handlers can't observe a
    /// torn (string, progress) pair when one book ends and the next begins.</summary>
    public CoordinatorStatus Current { get; private set; } = new(null, null);

    public string? CurrentStatus => Current.Text;
    public IndexProgress? CurrentProgress => Current.Progress;

    public IndexingBackgroundCoordinator(
        ILibraryService library,
        IEmbeddingStore store,
        IIndexingService indexing,
        MiniLmModelDownloader downloader)
    {
        _library = library;
        _store = store;
        _indexing = indexing;
        _downloader = downloader;
        _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _workerCts = new CancellationTokenSource();
        _library.BooksScanned += OnBooksScanned;
        _workerTask = Task.Run(() => WorkerLoopAsync(_workerCts.Token));
    }

    private async void OnBooksScanned(object? sender, IReadOnlyList<string> paths)
    {
        // Don't trigger an automatic download here — model fetch is the user's
        // explicit choice via Settings → Build index. Once the cache is populated,
        // subsequent scans will queue new books on this code path.
        if (!_downloader.IsDownloaded) return;

        try
        {
            // One round-trip to fetch every completed book_path; in-memory
            // filter the rest. Replaces N sequential IsBookIndexedAsync calls
            // (one open-connection-per-call each) when the library is large.
            var indexed = await _store.GetIndexedBookPathsAsync(_workerCts.Token).ConfigureAwait(false);
            foreach (var path in paths)
            {
                if (_workerCts.IsCancellationRequested) return;
                if (indexed.Contains(path)) continue;
                await _queue.Writer.WriteAsync(path, _workerCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[IndexingBackgroundCoordinator] OnBooksScanned failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string path;
            try { path = await _queue.Reader.ReadAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            // Yield to a manual Build that's already running. The IndexingService is
            // single-flight, so colliding here would just throw InvalidOperationException.
            while (_indexing.IsRunning && !ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            if (ct.IsCancellationRequested) break;

            try
            {
                if (await _store.IsBookIndexedAsync(path, ct).ConfigureAwait(false))
                {
                    if (_queue.Reader.Count == 0) Publish(null, null);
                    continue;
                }

                var title = Path.GetFileNameWithoutExtension(path);
                Publish($"Indexing '{title}'…", null);
                var progress = new Progress<IndexProgress>(p =>
                {
                    Publish(
                        p.ChunksTotal > 0
                            ? $"Indexing '{title}' — chunk {p.ChunksDone}/{p.ChunksTotal}"
                            : $"Indexing '{title}'…",
                        p);
                });
                await _indexing.IndexBookAsync(path, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[IndexingBackgroundCoordinator] book {Path.GetFileName(path)} failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (_queue.Reader.Count == 0) Publish(null, null);
        }
        Publish(null, null);
    }

    private void Publish(string? text, IndexProgress? progress)
    {
        var snapshot = new CoordinatorStatus(text, progress);
        Current = snapshot;
        StatusChanged?.Invoke(this, snapshot);
    }

    public sealed record CoordinatorStatus(string? Text, IndexProgress? Progress);

    public void Dispose()
    {
        _library.BooksScanned -= OnBooksScanned;
        _workerCts.Cancel();
        _queue.Writer.TryComplete();
        try { _workerTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }
        _workerCts.Dispose();
    }
}
