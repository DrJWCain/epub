using Epub_App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Epub_App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly ISettingsService _settings;

    public SettingsPage()
    {
        InitializeComponent();
        _settings = App.Current.Services.GetRequiredService<ISettingsService>();
        _settings.Changed += (s, e) => RefreshFolderRow();
        RefreshFolderRow();
    }

    private void RefreshFolderRow()
    {
        var path = _settings.LibraryFolderPath;
        FolderPathText.Text = string.IsNullOrEmpty(path) ? "(not set)" : path;
        ClearFolderButton.IsEnabled = !string.IsNullOrEmpty(path);
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
}
