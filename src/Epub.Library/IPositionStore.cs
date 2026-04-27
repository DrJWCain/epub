namespace Epub.Library;

public interface IPositionStore
{
    Task<ReadingPosition?> GetAsync(string bookPath, CancellationToken ct = default);
    Task SaveAsync(string bookPath, ReadingPosition position, CancellationToken ct = default);
}
