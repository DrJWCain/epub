using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
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

        // Keep NavView's highlight in sync with whatever page the Frame is on,
        // including back-arrow navigations. Without this hook the visual
        // selection lags after GoBack and assigning the same SelectedItem
        // again from code (e.g. NavigateToReaderTab) becomes a no-op.
        NavFrame.Navigated += OnNavFrameNavigated;
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

    private void GoBack_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
            args.Handled = true;
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        Type? targetType = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : args.SelectedItem is NavigationViewItem item
                ? TagToPageType(item.Tag?.ToString())
                : null;
        if (targetType is null) return;

        // Skip the redundant navigation when this SelectionChanged was raised
        // by us syncing the highlight after a back-arrow (the page is already
        // on screen). Without the guard, every Navigated → SelectedItem sync
        // would push another copy onto the back stack.
        if (NavFrame.CurrentSourcePageType != targetType)
            NavFrame.Navigate(targetType);
    }

    private void OnNavFrameNavigated(object sender, NavigationEventArgs e)
    {
        // Mirror the current Frame page back into NavView's selection. Pages
        // not in the menu (e.g. ClusterDetailPage, reached via Frame.Navigate
        // from DiscoverPage) leave the existing highlight alone.
        var tag = PageTypeToTag(e.SourcePageType);
        if (tag is null) return;

        if (tag == "settings")
        {
            if (!ReferenceEquals(NavView.SelectedItem, NavView.SettingsItem))
                NavView.SelectedItem = NavView.SettingsItem;
            return;
        }

        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem ni &&
                string.Equals(ni.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                if (!ReferenceEquals(NavView.SelectedItem, ni)) NavView.SelectedItem = ni;
                return;
            }
        }
    }

    private static Type? TagToPageType(string? tag) => tag switch
    {
        "library" => typeof(LibraryPage),
        "reader" => typeof(ReaderPage),
        "search" => typeof(SearchPage),
        "discover" => typeof(DiscoverPage),
        "threads" => typeof(ThreadPage),
        _ => null,
    };

    private static string? PageTypeToTag(Type? pageType)
    {
        if (pageType == typeof(LibraryPage)) return "library";
        if (pageType == typeof(ReaderPage)) return "reader";
        if (pageType == typeof(SearchPage)) return "search";
        if (pageType == typeof(DiscoverPage)) return "discover";
        if (pageType == typeof(ThreadPage)) return "threads";
        if (pageType == typeof(SettingsPage)) return "settings";
        return null;
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
                break;
            }
        }
        // If SelectedItem was already "reader" (e.g. the user navigated back
        // from Reader to ThreadPage and the visual highlight stayed on Reader)
        // the assignment above is a no-op and SelectionChanged doesn't fire.
        // Manually drive Frame so the navigation actually happens.
        if (NavFrame.CurrentSourcePageType != typeof(ReaderPage))
            NavFrame.Navigate(typeof(ReaderPage));
    }
}
