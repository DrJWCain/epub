using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Epub.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Epub_Renderer;

public sealed partial class ReaderControl : UserControl
{
    private const string SchemeName = "epub";
    private const string Authority = "book";

    private static readonly string ReaderScript = LoadEmbeddedResource("Epub_Renderer.Resources.reader.js");

    private EpubReader? _epubReader;
    private bool _webViewReady;

    public event EventHandler? SpineChanged;
    public event EventHandler? PageChanged;

    public int CurrentSpineIndex { get; private set; } = -1;
    public int SpineCount => _epubReader?.Book.Spine.Count ?? 0;
    public bool CanGoPrevChapter => CurrentSpineIndex > 0;
    public bool CanGoNextChapter => CurrentSpineIndex >= 0 && CurrentSpineIndex < SpineCount - 1;

    public int CurrentPageInChapter { get; private set; }
    public int ChapterPageCount { get; private set; } = 1;
    public bool CanGoForward => CanGoNextChapter || CurrentPageInChapter < ChapterPageCount - 1;
    public bool CanGoBack => CanGoPrevChapter || CurrentPageInChapter > 0;

    public ReaderControl()
    {
        InitializeComponent();
    }

    public async Task LoadBookAsync(EpubReader reader, int initialSpineIndex = 0, int initialPageInChapter = 0)
    {
        _epubReader = reader;
        CurrentSpineIndex = -1;
        CurrentPageInChapter = 0;
        ChapterPageCount = 1;
        await EnsureWebViewReadyAsync();

        var spine = Math.Clamp(initialSpineIndex, 0, Math.Max(0, reader.Book.Spine.Count - 1));
        var hash = initialPageInChapter > 0 ? $"#__page_{initialPageInChapter}" : null;
        ShowSpineItem(spine, hash);
    }

    /// <summary>Advance one page; spills into the next chapter at end of current.</summary>
    public async Task GoForwardAsync()
    {
        if (_epubReader is null || !_webViewReady) return;
        await WebView.CoreWebView2.ExecuteScriptAsync("window.Reader && window.Reader.nextPage()");
    }

    /// <summary>Retreat one page; spills into the previous chapter (last page) at start of current.</summary>
    public async Task GoBackAsync()
    {
        if (_epubReader is null || !_webViewReady) return;
        await WebView.CoreWebView2.ExecuteScriptAsync("window.Reader && window.Reader.prevPage()");
    }

    public void ShowSpineItem(int index, string? hashFragment = null)
    {
        if (_epubReader is null) return;
        if (index < 0 || index >= _epubReader.Book.Spine.Count) return;

        var item = _epubReader.Book.Spine[index];
        var zipPath = _epubReader.ResolveHref(item.ManifestItem.Href);
        var url = $"{SchemeName}://{Authority}/{zipPath}";
        if (!string.IsNullOrEmpty(hashFragment))
            url += hashFragment.StartsWith('#') ? hashFragment : "#" + hashFragment;
        Debug.WriteLine($"[ReaderControl] Navigating to: {url}");
        WebView.CoreWebView2.Navigate(url);

        CurrentSpineIndex = index;
        CurrentPageInChapter = 0;
        ChapterPageCount = 1;
        SpineChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Navigate to a TOC href (already an absolute zip path from NavParser/NcxParser, may include #anchor).
    /// Returns true if a matching spine item was found.
    /// </summary>
    public bool TryNavigateToHref(string absoluteHref)
    {
        if (_epubReader is null || string.IsNullOrEmpty(absoluteHref)) return false;

        string pathPart = absoluteHref;
        string? hashFragment = null;
        var hashIdx = absoluteHref.IndexOf('#');
        if (hashIdx >= 0)
        {
            pathPart = absoluteHref[..hashIdx];
            hashFragment = absoluteHref[hashIdx..]; // includes the '#'
        }

        for (int i = 0; i < _epubReader.Book.Spine.Count; i++)
        {
            var spineResolved = _epubReader.ResolveHref(_epubReader.Book.Spine[i].ManifestItem.Href);
            if (string.Equals(spineResolved, pathPart, StringComparison.Ordinal))
            {
                ShowSpineItem(i, hashFragment);
                return true;
            }
        }
        return false;
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

        await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ReaderScript);

        WebView.CoreWebView2.AddWebResourceRequestedFilter(
            $"{SchemeName}://*",
            CoreWebView2WebResourceContext.All);
        WebView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
        WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        WebView.CoreWebView2.NavigationStarting += (s, e) =>
            Debug.WriteLine($"[ReaderControl] NavigationStarting: {e.Uri}");
        WebView.CoreWebView2.NavigationCompleted += (s, e) =>
            Debug.WriteLine($"[ReaderControl] NavigationCompleted: success={e.IsSuccess}, status={e.WebErrorStatus}, httpStatus={e.HttpStatusCode}");

        _webViewReady = true;
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var json = args.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            switch (type)
            {
                case "pageChanged":
                    CurrentPageInChapter = doc.RootElement.GetProperty("page").GetInt32();
                    ChapterPageCount = doc.RootElement.GetProperty("total").GetInt32();
                    Debug.WriteLine($"[ReaderControl] pageChanged: {CurrentPageInChapter + 1}/{ChapterPageCount}");
                    PageChanged?.Invoke(this, EventArgs.Empty);
                    break;

                case "endOfChapter":
                    Debug.WriteLine("[ReaderControl] endOfChapter — advancing to next spine item");
                    if (CanGoNextChapter)
                        ShowSpineItem(CurrentSpineIndex + 1);
                    break;

                case "startOfChapter":
                    Debug.WriteLine("[ReaderControl] startOfChapter — going back to previous spine item, last page");
                    if (CanGoPrevChapter)
                        ShowSpineItem(CurrentSpineIndex - 1, hashFragment: "#__last_page");
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ReaderControl] WebMessage parse error: {ex.GetType().Name}: {ex.Message}");
        }
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

    private static string LoadEmbeddedResource(string resourceName)
    {
        var asm = typeof(ReaderControl).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
