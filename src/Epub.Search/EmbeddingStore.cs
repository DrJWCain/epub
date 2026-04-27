using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Epub.Search;

public sealed class EmbeddingStore : IEmbeddingStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _schemaInitialized;

    public EmbeddingStore(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={databasePath}";
    }

    public async Task<IIndexSession> BeginIndexAsync(string bookPath, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        long size = 0, mtime = 0;
        if (File.Exists(bookPath))
        {
            var fi = new FileInfo(bookPath);
            size = fi.Length;
            mtime = ((DateTimeOffset)fi.LastWriteTimeUtc).ToUnixTimeSeconds();
        }

        var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            long bookId;
            await using (var upsert = connection.CreateCommand())
            {
                upsert.CommandText = """
                    INSERT INTO books_indexed(book_path, file_size, file_mtime, completed_at)
                    VALUES($path, $size, $mtime, NULL)
                    ON CONFLICT(book_path) DO UPDATE SET
                        file_size = excluded.file_size,
                        file_mtime = excluded.file_mtime,
                        completed_at = NULL
                    RETURNING book_id
                    """;
                upsert.Parameters.AddWithValue("$path", bookPath);
                upsert.Parameters.AddWithValue("$size", size);
                upsert.Parameters.AddWithValue("$mtime", mtime);
                bookId = Convert.ToInt64(await upsert.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            // Wipe any prior partial chunks for this book before re-indexing
            // (CASCADE on the FK takes care of embeddings rows).
            await using (var wipe = connection.CreateCommand())
            {
                wipe.CommandText = "DELETE FROM chunks WHERE book_id = $bid";
                wipe.Parameters.AddWithValue("$bid", bookId);
                await wipe.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var transaction = (SqliteTransaction)
                await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            return new IndexSession(bookId, connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> IsBookIndexedAsync(string bookPath, CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT completed_at FROM books_indexed WHERE book_path = $p";
        cmd.Parameters.AddWithValue("$p", bookPath);
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null && result is not DBNull;
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding, int k, CancellationToken ct = default)
    {
        if (k <= 0) return Array.Empty<SearchHit>();
        await EnsureSchemaAsync(ct).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        // Brute-force scan: read every (chunk_id, vec) pair and keep the top-K by cosine.
        // Uses System.Numerics.Vector<float> SIMD inside CosineSim.
        var heap = new SortedDictionary<(float Sim, long Id), bool>();

        await using (var scan = connection.CreateCommand())
        {
            scan.CommandText = "SELECT chunk_id, vec FROM embeddings";
            await using var reader = await scan.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var chunkId = reader.GetInt64(0);
                var blob = (byte[])reader["vec"];
                var sim = CosineSim(queryEmbedding.Span, MemoryMarshal.Cast<byte, float>(blob));

                if (heap.Count < k)
                {
                    heap[(sim, chunkId)] = true;
                }
                else
                {
                    var min = heap.Keys.First();
                    if (sim > min.Sim)
                    {
                        heap.Remove(min);
                        heap[(sim, chunkId)] = true;
                    }
                }
            }
        }

        if (heap.Count == 0) return Array.Empty<SearchHit>();

        // Hydrate the top-K with chunk + book metadata in a single round-trip.
        var orderedIds = heap.Reverse().Select(kv => kv.Key.Id).ToList();
        var simByChunkId = heap.ToDictionary(kv => kv.Key.Id, kv => kv.Key.Sim);

        var placeholders = string.Join(",", Enumerable.Range(0, orderedIds.Count).Select(i => $"$id{i}"));
        await using var hydrate = connection.CreateCommand();
        hydrate.CommandText = $"""
            SELECT c.id, c.book_id, c.spine_idx, c.char_offset, c.char_length, c.text, b.book_path
            FROM chunks c
            JOIN books_indexed b ON b.book_id = c.book_id
            WHERE c.id IN ({placeholders})
            """;
        for (int i = 0; i < orderedIds.Count; i++)
            hydrate.Parameters.AddWithValue($"$id{i}", orderedIds[i]);

        var byId = new Dictionary<long, SearchHit>(orderedIds.Count);
        await using (var hr = await hydrate.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await hr.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = hr.GetInt64(0);
                var text = hr.GetString(5);
                byId[id] = new SearchHit(
                    ChunkId: id,
                    BookId: hr.GetInt64(1),
                    BookPath: hr.GetString(6),
                    SpineIdx: hr.GetInt32(2),
                    CharOffset: hr.GetInt32(3),
                    CharLength: hr.GetInt32(4),
                    Snippet: Snippet(text),
                    Similarity: simByChunkId[id]);
            }
        }

        return orderedIds.Select(id => byId[id]).ToList();
    }

    public async Task<IndexMeta> GetMetaAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        string? model = null;
        long? lastBuildEpoch = null;

        await using (var meta = connection.CreateCommand())
        {
            meta.CommandText = "SELECT key, value FROM index_meta WHERE key IN ('model','last_build_at')";
            await using var reader = await meta.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                switch (reader.GetString(0))
                {
                    case "model": model = reader.GetString(1); break;
                    case "last_build_at": lastBuildEpoch = long.Parse(reader.GetString(1)); break;
                }
            }
        }

        int bookCount;
        await using (var bc = connection.CreateCommand())
        {
            bc.CommandText = "SELECT COUNT(*) FROM books_indexed WHERE completed_at IS NOT NULL";
            bookCount = Convert.ToInt32(await bc.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        long chunkCount;
        await using (var cc = connection.CreateCommand())
        {
            cc.CommandText = "SELECT COUNT(*) FROM chunks";
            chunkCount = Convert.ToInt64(await cc.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }

        return new IndexMeta(
            Model: model,
            IndexedBookCount: bookCount,
            ChunkCount: chunkCount,
            LastBuildAt: lastBuildEpoch is null ? null : DateTimeOffset.FromUnixTimeSeconds(lastBuildEpoch.Value));
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_schemaInitialized) return;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_schemaInitialized) return;
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

            await using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode = WAL";
                await wal.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var ddl = connection.CreateCommand())
            {
                ddl.CommandText = """
                    CREATE TABLE IF NOT EXISTS books_indexed (
                        book_id      INTEGER PRIMARY KEY,
                        book_path    TEXT NOT NULL UNIQUE COLLATE NOCASE,
                        file_size    INTEGER NOT NULL,
                        file_mtime   INTEGER NOT NULL,
                        completed_at INTEGER
                    );

                    CREATE TABLE IF NOT EXISTS chunks (
                        id           INTEGER PRIMARY KEY,
                        book_id      INTEGER NOT NULL REFERENCES books_indexed(book_id) ON DELETE CASCADE,
                        spine_idx    INTEGER NOT NULL,
                        char_offset  INTEGER NOT NULL,
                        char_length  INTEGER NOT NULL,
                        text         TEXT NOT NULL,
                        added_at     INTEGER NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_chunks_book ON chunks(book_id);

                    CREATE TABLE IF NOT EXISTS embeddings (
                        chunk_id  INTEGER PRIMARY KEY REFERENCES chunks(id) ON DELETE CASCADE,
                        vec       BLOB NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS index_meta (
                        key   TEXT PRIMARY KEY,
                        value TEXT NOT NULL
                    );
                    """;
                await ddl.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            _schemaInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static string Snippet(string text)
    {
        const int charBudget = 240;
        if (text.Length <= charBudget) return text.Trim();

        // Prefer ending on a sentence boundary if one is reachable within the budget.
        var window = text.AsSpan(0, charBudget);
        var lastTerminator = -1;
        for (int i = window.Length - 1; i >= 0; i--)
        {
            var c = window[i];
            if (c == '.' || c == '!' || c == '?')
            {
                lastTerminator = i;
                break;
            }
        }
        if (lastTerminator > charBudget / 2)
            return text.Substring(0, lastTerminator + 1).Trim();
        return text.Substring(0, charBudget).TrimEnd() + "…";
    }

    private static float CosineSim(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException(
                $"Vector dimension mismatch: query={a.Length}, stored={b.Length}.");

        float dot = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            int simdEnd = a.Length - (a.Length % Vector<float>.Count);
            for (; i < simdEnd; i += Vector<float>.Count)
            {
                var va = new Vector<float>(a.Slice(i, Vector<float>.Count));
                var vb = new Vector<float>(b.Slice(i, Vector<float>.Count));
                acc += va * vb;
            }
            dot = Vector.Dot(acc, Vector<float>.One);
        }
        for (; i < a.Length; i++) dot += a[i] * b[i];

        return dot;
    }

    private static byte[] FloatsToBlob(ReadOnlySpan<float> floats)
        => MemoryMarshal.AsBytes(floats).ToArray();

    private sealed class IndexSession : IIndexSession
    {
        private readonly SqliteConnection _connection;
        private SqliteTransaction? _transaction;
        private bool _completed;
        private bool _disposed;

        public long BookId { get; }

        public IndexSession(long bookId, SqliteConnection connection, SqliteTransaction transaction)
        {
            BookId = bookId;
            _connection = connection;
            _transaction = transaction;
        }

        public async Task AppendChunkAsync(int spineIdx, int charOffset, int charLength, string text,
            ReadOnlyMemory<float> embedding, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_transaction is null)
                throw new InvalidOperationException("Session has been completed or rolled back.");

            long chunkId;
            await using (var ic = _connection.CreateCommand())
            {
                ic.Transaction = _transaction;
                ic.CommandText = """
                    INSERT INTO chunks(book_id, spine_idx, char_offset, char_length, text, added_at)
                    VALUES($bid, $sp, $co, $cl, $tx, $at)
                    RETURNING id
                    """;
                ic.Parameters.AddWithValue("$bid", BookId);
                ic.Parameters.AddWithValue("$sp", spineIdx);
                ic.Parameters.AddWithValue("$co", charOffset);
                ic.Parameters.AddWithValue("$cl", charLength);
                ic.Parameters.AddWithValue("$tx", text);
                ic.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                chunkId = Convert.ToInt64(await ic.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            await using var ie = _connection.CreateCommand();
            ie.Transaction = _transaction;
            ie.CommandText = "INSERT INTO embeddings(chunk_id, vec) VALUES($cid, $v)";
            ie.Parameters.AddWithValue("$cid", chunkId);
            ie.Parameters.AddWithValue("$v", FloatsToBlob(embedding.Span));
            await ie.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task CompleteAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_transaction is null)
                throw new InvalidOperationException("Session has already been completed.");

            var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using (var stamp = _connection.CreateCommand())
            {
                stamp.Transaction = _transaction;
                stamp.CommandText = "UPDATE books_indexed SET completed_at = $at WHERE book_id = $bid";
                stamp.Parameters.AddWithValue("$at", nowEpoch);
                stamp.Parameters.AddWithValue("$bid", BookId);
                await stamp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            await using (var meta = _connection.CreateCommand())
            {
                meta.Transaction = _transaction;
                meta.CommandText = "INSERT OR REPLACE INTO index_meta(key, value) VALUES('last_build_at', $v)";
                meta.Parameters.AddWithValue("$v", nowEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await meta.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await _transaction.CommitAsync(ct).ConfigureAwait(false);
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            if (_transaction is not null)
            {
                if (!_completed)
                {
                    try { await _transaction.RollbackAsync().ConfigureAwait(false); }
                    catch { /* ignore: rollback on a closed connection or after error */ }
                }
                await _transaction.DisposeAsync().ConfigureAwait(false);
            }
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
