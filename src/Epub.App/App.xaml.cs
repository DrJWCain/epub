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
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
