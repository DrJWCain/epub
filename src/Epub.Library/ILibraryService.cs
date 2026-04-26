namespace Epub.Library;

public interface ILibraryService
{
    /// <summary>
    /// Scan a folder for .epub files, returning a LibraryEntry per book.
    /// Books that fail to parse are skipped. Returns empty if folder is missing.
    /// </summary>
    Task<IReadOnlyList<LibraryEntry>> ScanFolderAsync(string folderPath, CancellationToken ct = default);
}
