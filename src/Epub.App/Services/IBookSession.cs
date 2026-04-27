namespace Epub_App.Services;

/// <summary>
/// One-shot hand-off for the file path the user wants to open. Library page sets
/// the path; Reader page consumes (and clears) it on OnNavigatedTo. Search results
/// also set <see cref="PendingSpineIndex"/> + <see cref="PendingCharOffset"/> so
/// the reader can land on the matching paragraph rather than the saved position.
/// </summary>
public interface IBookSession
{
    string? PendingBookPath { get; set; }
    int? PendingSpineIndex { get; set; }
    int? PendingCharOffset { get; set; }
}

internal sealed class BookSession : IBookSession
{
    public string? PendingBookPath { get; set; }
    public int? PendingSpineIndex { get; set; }
    public int? PendingCharOffset { get; set; }
}
