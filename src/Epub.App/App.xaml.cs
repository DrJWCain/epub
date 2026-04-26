using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;

namespace Epub_App;

public partial class App : Application
{
    private Window? _window;

    public IHost Host { get; }

    public new static App Current => (App)Application.Current;

    public IServiceProvider Services => Host.Services;

    public App()
    {
        InitializeComponent();

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        ConfigureServices(builder.Services);
        Host = builder.Build();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Service registrations land here as features come online.
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
