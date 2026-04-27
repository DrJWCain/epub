using Epub.Core;

namespace Epub.Library;

public sealed class LibraryService : ILibraryService
{
    public event EventHandler<IReadOnlyList<string>>? BooksScanned;

    public async Task<IReadOnlyList<LibraryEntry>> ScanFolderAsync(string folderPath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return Array.Empty<LibraryEntry>();

        var paths = Directory.EnumerateFiles(folderPath, "*.epub", SearchOption.TopDirectoryOnly)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var entries = new List<LibraryEntry>(paths.Count);
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entry = await Task.Run(() => LoadEntry(path), ct);
                entries.Add(entry);
            }
            catch
            {
                // Skip unreadable EPUBs silently — the alternative is to crash the whole scan.
            }
        }

        if (entries.Count > 0)
        {
            var paths2 = entries.Select(e => e.FilePath).ToList();
            try { BooksScanned?.Invoke(this, paths2); }
            catch { /* never let a subscriber crash the scan */ }
        }

        return entries;
    }

    private static LibraryEntry LoadEntry(string filePath)
    {
        using var reader = EpubReader.Open(filePath);
        var book = reader.Book;

        byte[]? coverBytes = null;
        string? coverMime = null;
        if (book.CoverImageHref is { } coverHref)
        {
            try
            {
                var coverPath = reader.ResolveHref(coverHref);
                using var stream = reader.OpenResource(coverPath);
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                coverBytes = ms.ToArray();
                coverMime = book.Manifest
                    .FirstOrDefault(m => string.Equals(m.Href, coverHref, StringComparison.Ordinal))
                    ?.MediaType;
            }
            catch
            {
                // Cover declared but unreadable — leave bytes null, UI will fall back.
            }
        }

        return new LibraryEntry
        {
            FilePath = filePath,
            Title = book.Metadata.Title,
            Authors = book.Metadata.Authors,
            CoverImageBytes = coverBytes,
            CoverImageMediaType = coverMime,
        };
    }
}
