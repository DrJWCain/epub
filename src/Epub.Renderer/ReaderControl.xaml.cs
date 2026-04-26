using System.Diagnostics;
using Epub.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Epub_Renderer;

public sealed partial class ReaderControl : UserControl
{
    private const string SchemeName = "epub";
    private const string Authority = "book";

    private EpubReader? _epubReader;
    private bool _webViewReady;

    public event EventHandler? SpineChanged;

    public int CurrentSpineIndex { get; private set; } = -1;
    public int SpineCount => _epubReader?.Book.Spine.Count ?? 0;
    public bool CanGoPrev => CurrentSpineIndex > 0;
    public bool CanGoNext => CurrentSpineIndex >= 0 && CurrentSpineIndex < SpineCount - 1;

    public ReaderControl()
    {
        InitializeComponent();
    }

    public async Task LoadBookAsync(EpubReader reader)
    {
        _epubReader = reader;
        CurrentSpineIndex = -1;
        await EnsureWebViewReadyAsync();
        ShowSpineItem(0);
    }

    public void GoPrev() => ShowSpineItem(CurrentSpineIndex - 1);
    public void GoNext() => ShowSpineItem(CurrentSpineIndex + 1);

    public void ShowSpineItem(int index)
    {
        if (_epubReader is null) return;
        if (index < 0 || index >= _epubReader.Book.Spine.Count) return;

        var item = _epubReader.Book.Spine[index];
        var zipPath = _epubReader.ResolveHref(item.ManifestItem.Href);
        var url = $"{SchemeName}://{Authority}/{zipPath}";
        Debug.WriteLine($"[ReaderControl] Navigating to: {url}");
        WebView.CoreWebView2.Navigate(url);

        CurrentSpineIndex = index;
        SpineChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task EnsureWebViewReadyAsync()
    {
        if (_webViewReady) return;

        var registration = new CoreWebView2CustomSchemeRegistration(SchemeName);
        registration.TreatAsSecure = 1;
        registration.HasAuthorityComponent = true;
        registration.AllowedOrigins.Add("*");

        var options = new CoreWebView2EnvironmentOptions();
        options.CustomSchemeRegistrations = new List<CoreWebView2CustomSchemeRegistration> { registration };
        Debug.WriteLine($"[ReaderControl] Registered scheme '{SchemeName}', registrations count = {options.CustomSchemeRegistrations.Count}");

        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
            browserExecutableFolder: null,
            userDataFolder: null,
            options: options);

        await WebView.EnsureCoreWebView2Async(environment);

        WebView.CoreWebView2.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Light;

        WebView.CoreWebView2.AddWebResourceRequestedFilter(
            $"{SchemeName}://*",
            CoreWebView2WebResourceContext.All);
        WebView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
        WebView.CoreWebView2.NavigationStarting += (s, e) =>
            Debug.WriteLine($"[ReaderControl] NavigationStarting: {e.Uri}");
        WebView.CoreWebView2.NavigationCompleted += (s, e) =>
            Debug.WriteLine($"[ReaderControl] NavigationCompleted: success={e.IsSuccess}, status={e.WebErrorStatus}, httpStatus={e.HttpStatusCode}");

        _webViewReady = true;
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_epubReader is null)
        {
            Debug.WriteLine($"[ReaderControl] Resource requested but no reader: {args.Request.Uri}");
            return;
        }

        var requestUri = new Uri(args.Request.Uri);
        var zipPath = Uri.UnescapeDataString(requestUri.AbsolutePath.TrimStart('/'));

        try
        {
            using var resource = _epubReader.OpenResource(zipPath);
            var ms = new MemoryStream();
            resource.CopyTo(ms);
            ms.Position = 0;

            var mime = ResolveMime(zipPath);
            var headers = $"Content-Type: {mime}";

            args.Response = sender.Environment.CreateWebResourceResponse(
                ms.AsRandomAccessStream(), 200, "OK", headers);
            Debug.WriteLine($"[ReaderControl] 200 {mime} ({ms.Length} bytes) for {zipPath}");
        }
        catch (FileNotFoundException)
        {
            args.Response = sender.Environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
            Debug.WriteLine($"[ReaderControl] 404 for {zipPath}");
        }
        catch (Exception ex)
        {
            args.Response = sender.Environment.CreateWebResourceResponse(null, 500, "Internal Error", string.Empty);
            Debug.WriteLine($"[ReaderControl] 500 for {zipPath}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private string ResolveMime(string zipPath)
    {
        if (_epubReader is not null)
        {
            foreach (var item in _epubReader.Book.Manifest)
            {
                if (string.Equals(_epubReader.ResolveHref(item.Href), zipPath, StringComparison.Ordinal))
                    return item.MediaType;
            }
        }
        return MimeTypes.FromExtension(Path.GetExtension(zipPath));
    }

}
