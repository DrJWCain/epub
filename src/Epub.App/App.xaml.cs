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

    public App()
    {
        InitializeComponent();

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        ConfigureServices(builder.Services);
        Host = builder.Build();
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
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
