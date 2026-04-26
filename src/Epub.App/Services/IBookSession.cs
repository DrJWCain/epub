namespace Epub_App.Services;

/// <summary>
/// One-shot hand-off for the file path the user wants to open. Library page sets it,
/// then triggers navigation; Reader page consumes (and clears) it on OnNavigatedTo.
/// </summary>
public interface IBookSession
{
    string? PendingBookPath { get; set; }
}

internal sealed class BookSession : IBookSession
{
    public string? PendingBookPath { get; set; }
}
