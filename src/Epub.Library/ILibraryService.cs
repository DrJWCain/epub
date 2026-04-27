namespace Epub.Library;

public interface ILibraryService
{
    /// <summary>
    /// Scan a folder for .epub files, returning a LibraryEntry per book.
    /// Books that fail to parse are skipped. Returns empty if folder is missing.
    /// </summary>
    Task<IReadOnlyList<LibraryEntry>> ScanFolderAsync(string folderPath, CancellationToken ct = default);

    /// <summary>
    /// Raised at the end of a successful scan with the absolute paths of every EPUB
    /// the scan found. Subscribers (e.g. the indexing coordinator) can filter against
    /// what they've already processed and act on the rest. Kept ignorant of indexing
    /// state so this project stays leaf — no reference back to Epub.Search.
    /// </summary>
    event EventHandler<IReadOnlyList<string>>? BooksScanned;
}
