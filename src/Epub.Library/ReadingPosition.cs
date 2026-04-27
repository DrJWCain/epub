namespace Epub.Library;

public sealed record ReadingPosition(int SpineIndex, int PageInChapter, DateTimeOffset UpdatedAt);
