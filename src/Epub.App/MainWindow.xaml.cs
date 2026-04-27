using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Epub_App.Pages;
using Epub_App.Services;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Epub_App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }

        // Eagerly construct the background indexing coordinator so its subscription
        // to LibraryService.BooksScanned is wired up before the first scan fires.
        App.Current.Services.GetRequiredService<IndexingBackgroundCoordinator>();
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void ToggleFullScreen_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            if (AppWindow.Presenter is OverlappedPresenter overlapped)
            {
                overlapped.Maximize();
            }
            AppTitleBar.Visibility = Visibility.Visible;
        }
        else
        {
            AppTitleBar.Visibility = Visibility.Collapsed;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "library":
                    NavFrame.Navigate(typeof(LibraryPage));
                    break;
                case "reader":
                    NavFrame.Navigate(typeof(ReaderPage));
                    break;
                case "search":
                    NavFrame.Navigate(typeof(SearchPage));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
            }
        }
    }

    /// <summary>Set the title bar text. Pass null to reset to the app default.</summary>
    public void SetTitleBarTitle(string? title)
    {
        var resolved = string.IsNullOrWhiteSpace(title) ? "Epub.App" : title;
        AppTitleBar.Title = resolved;
        Title = resolved;
    }

    /// <summary>Switch the NavigationView to the Reader tab. Used by Library when the user clicks a book.</summary>
    public void NavigateToReaderTab()
    {
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem ni && string.Equals(ni.Tag?.ToString(), "reader", StringComparison.Ordinal))
            {
                NavView.SelectedItem = ni;
                return;
            }
        }
    }
}
