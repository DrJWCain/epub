using Epub_App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;

namespace Epub_App;

public partial class App : Application
{
    public IHost Host { get; }

    public new static App Current => (App)Application.Current;

    public IServiceProvider Services => Host.Services;

    public Window? MainWindow { get; private set; }

    // Diagnostic crash log. WinUI surfaces unhandled UI-thread exceptions as a
    // "stowed exception" fail-fast (0xc000027b) that hides the real error from
    // the event log. Capturing them here writes the actual exception to disk.
    private static readonly string CrashLogPath =
        Path.Combine(Path.GetTempPath(), "epub-crash.log");

    public App()
    {
        InitializeComponent();

        UnhandledException += (s, e) =>
            // Log the real exception (WinUI otherwise reports only an opaque
            // 0xc000027b stowed-exception fail-fast), then let it crash normally —
            // swallowing it would leave the app wedged in a half-broken state.
            LogCrash($"Xaml.UnhandledException: {e.Message}", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        ConfigureServices(builder.Services);
        Host = builder.Build();
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* never let logging throw */ }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IBookSession, BookSession>();
        services.AddSingleton<Epub.Library.ILibraryService, Epub.Library.LibraryService>();

        var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        var dbPath = Path.Combine(localFolder, "library.db");
        var modelDir = Path.Combine(localFolder, "models", "minilm");

        services.AddSingleton<Epub.Library.IPositionStore>(_ =>
            new Epub.Library.PositionStore(dbPath));

        services.AddSingleton<Epub.Search.Embedding.MiniLmModelDownloader>(_ =>
            new Epub.Search.Embedding.MiniLmModelDownloader(modelDir));
        services.AddSingleton<Epub.Search.Embedding.MiniLmEmbedder>();
        services.AddSingleton<Epub.Search.IEmbeddingStore>(_ =>
            new Epub.Search.EmbeddingStore(dbPath));
        services.AddSingleton<Epub.Search.IIndexingService, Epub.Search.IndexingService>();
        services.AddSingleton<Epub.Search.Clustering.IClusteringService, Epub.Search.Clustering.ClusteringService>();
        services.AddSingleton<IndexingBackgroundCoordinator>();

        var phi4Dir = Path.Combine(localFolder, "models", "phi4-mini");
        services.AddSingleton<Epub.Search.Llm.Phi4ModelDownloader>(_ =>
            new Epub.Search.Llm.Phi4ModelDownloader(phi4Dir));
        services.AddSingleton<Epub.Search.Llm.Phi4Generator>();
        services.AddSingleton<Epub.Search.Llm.ConceptThreadService>();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
