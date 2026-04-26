namespace Epub_App.Services;

public interface ISettingsService
{
    string? LibraryFolderPath { get; set; }
    event EventHandler? Changed;
}
