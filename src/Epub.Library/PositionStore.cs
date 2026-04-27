using Microsoft.Data.Sqlite;

namespace Epub.Library;

public sealed class PositionStore : IPositionStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _schemaInitialized;

    public PositionStore(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={databasePath}";
    }

    public async Task<ReadingPosition?> GetAsync(string bookPath, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT spine_index, page_in_chapter, updated_at FROM reading_positions WHERE book_path = $path";
        cmd.Parameters.AddWithValue("$path", bookPath);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return new ReadingPosition(
            SpineIndex: reader.GetInt32(0),
            PageInChapter: reader.GetInt32(1),
            UpdatedAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)));
    }

    public async Task SaveAsync(string bookPath, ReadingPosition position, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO reading_positions(book_path, spine_index, page_in_chapter, updated_at)
            VALUES($path, $spine, $page, $ts)
            ON CONFLICT(book_path) DO UPDATE SET
                spine_index = excluded.spine_index,
                page_in_chapter = excluded.page_in_chapter,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$path", bookPath);
        cmd.Parameters.AddWithValue("$spine", position.SpineIndex);
        cmd.Parameters.AddWithValue("$page", position.PageInChapter);
        cmd.Parameters.AddWithValue("$ts", position.UpdatedAt.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaInitialized) return;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_schemaInitialized) return;
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS reading_positions (
                    book_path        TEXT PRIMARY KEY COLLATE NOCASE,
                    spine_index      INTEGER NOT NULL,
                    page_in_chapter  INTEGER NOT NULL,
                    updated_at       INTEGER NOT NULL
                )
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _schemaInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
