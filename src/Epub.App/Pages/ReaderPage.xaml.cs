using Epub.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Epub_App.Pages;

public sealed partial class ReaderPage : Page
{
    public ReaderPage()
    {
        InitializeComponent();
        ReaderControl.PageChanged += (s, e) => UpdateNavState();
        ReaderControl.SpineChanged += (s, e) => UpdateNavState();
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

        var reader = await EpubReader.OpenAsync(file.Path);
        EmptyState.Visibility = Visibility.Collapsed;
        ReaderControl.Visibility = Visibility.Visible;
        await ReaderControl.LoadBookAsync(reader);
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) => await ReaderControl.GoBackAsync();

    private async void Next_Click(object sender, RoutedEventArgs e) => await ReaderControl.GoForwardAsync();
}
