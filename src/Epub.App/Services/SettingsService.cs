using Windows.Storage;

namespace Epub_App.Services;

/// <summary>Backed by ApplicationData.LocalSettings — survives app restarts and is per-user, per-package.</summary>
internal sealed class SettingsService : ISettingsService
{
    private const string LibraryFolderPathKey = "LibraryFolderPath";

    private readonly ApplicationDataContainer _container = ApplicationData.Current.LocalSettings;

    public event EventHandler? Changed;

    public string? LibraryFolderPath
    {
        get => _container.Values[LibraryFolderPathKey] as string;
        set
        {
            if (string.Equals(LibraryFolderPath, value, StringComparison.Ordinal)) return;
            if (string.IsNullOrEmpty(value))
                _container.Values.Remove(LibraryFolderPathKey);
            else
                _container.Values[LibraryFolderPathKey] = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
