namespace Epub_App.Services;

/// <summary>
/// One-shot hand-off for the file path the user wants to open. Library page sets
/// the path; Reader page consumes (and clears) it on OnNavigatedTo. Search results
/// also set <see cref="PendingSpineIndex"/> + <see cref="PendingProbeText"/> so
/// the reader can find the matching passage in the rendered DOM rather than
/// restoring the saved position.
/// </summary>
public interface IBookSession
{
    string? PendingBookPath { get; set; }
    int? PendingSpineIndex { get; set; }
    string? PendingProbeText { get; set; }
}

internal sealed class BookSession : IBookSession
{
    public string? PendingBookPath { get; set; }
    public int? PendingSpineIndex { get; set; }
    public string? PendingProbeText { get; set; }
}
