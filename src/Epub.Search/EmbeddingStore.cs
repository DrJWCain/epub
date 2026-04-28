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
        // No DB work yet — just hand back a buffered session. Append calls
        // accumulate in RAM; Complete does the whole upsert+wipe+insert
        // dance in one short transaction. Keeps the SQLite write lock held
        // only briefly rather than for the full embedding run.
        return new IndexSession(this, bookPath);
    }

    private async Task PersistBookAsync(
        string bookPath,
        IReadOnlyList<BufferedChunk> chunks,
        CancellationToken ct)
    {
        long size = 0, mtime = 0;
        if (File.Exists(bookPath))
        {
            var fi = new FileInfo(bookPath);
            size = fi.Length;
            mtime = ((DateTimeOffset)fi.LastWriteTimeUtc).ToUnixTimeSeconds();
        }

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)
            await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        long bookId;
        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = tx;
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

        // Wipe any prior chunks for this book — covers re-index of either
        // a previously-completed run or a half-finished prior attempt.
        // CASCADE on the FK takes care of embeddings rows.
        await using (var wipe = connection.CreateCommand())
        {
            wipe.Transaction = tx;
            wipe.CommandText = "DELETE FROM chunks WHERE book_id = $bid";
            wipe.Parameters.AddWithValue("$bid", bookId);
            await wipe.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var addedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using (var insertChunk = connection.CreateCommand())
        await using (var insertEmb = connection.CreateCommand())
        {
            insertChunk.Transaction = tx;
            insertChunk.CommandText = """
                INSERT INTO chunks(book_id, spine_idx, char_offset, char_length, text, added_at)
                VALUES($bid, $sp, $co, $cl, $tx, $at)
                RETURNING id
                """;
            var bidP = insertChunk.Parameters.Add("$bid", SqliteType.Integer);
            var spP = insertChunk.Parameters.Add("$sp", SqliteType.Integer);
            var coP = insertChunk.Parameters.Add("$co", SqliteType.Integer);
            var clP = insertChunk.Parameters.Add("$cl", SqliteType.Integer);
            var txP = insertChunk.Parameters.Add("$tx", SqliteType.Text);
            var atP = insertChunk.Parameters.Add("$at", SqliteType.Integer);

            insertEmb.Transaction = tx;
            insertEmb.CommandText = "INSERT INTO embeddings(chunk_id, vec) VALUES($cid, $v)";
            var cidP = insertEmb.Parameters.Add("$cid", SqliteType.Integer);
            var vP = insertEmb.Parameters.Add("$v", SqliteType.Blob);

            bidP.Value = bookId;
            atP.Value = addedAtMs;
            foreach (var c in chunks)
            {
                ct.ThrowIfCancellationRequested();
                spP.Value = c.SpineIdx;
                coP.Value = c.CharOffset;
                clP.Value = c.CharLength;
                txP.Value = c.Text;
                var chunkId = Convert.ToInt64(
                    await insertChunk.ExecuteScalarAsync(ct).ConfigureAwait(false));
                cidP.Value = chunkId;
                vP.Value = FloatsToBlob(c.Embedding);
                await insertEmb.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using (var stamp = connection.CreateCommand())
        {
            stamp.Transaction = tx;
            stamp.CommandText = "UPDATE books_indexed SET completed_at = $at WHERE book_id = $bid";
            stamp.Parameters.AddWithValue("$at", nowEpoch);
            stamp.Parameters.AddWithValue("$bid", bookId);
            await stamp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var meta = connection.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = "INSERT OR REPLACE INTO index_meta(key, value) VALUES('last_build_at', $v)";
            meta.Parameters.AddWithValue("$v", nowEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await meta.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private sealed record BufferedChunk(
        int SpineIdx, int CharOffset, int CharLength, string Text, float[] Embedding);

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

    public async Task<IReadOnlySet<string>> GetIndexedBookPathsAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT book_path FROM books_indexed WHERE completed_at IS NOT NULL";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            paths.Add(reader.GetString(0));
        return paths;
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

    public async Task EnumerateEmbeddingsAsync(
        Func<long, ReadOnlyMemory<float>, CancellationToken, Task> onPair,
        CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT chunk_id, vec FROM embeddings";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var chunkId = reader.GetInt64(0);
            var blob = (byte[])reader["vec"];
            await onPair(chunkId, BlobToFloats(blob), ct).ConfigureAwait(false);
        }
    }

    public async Task RewriteClustersAsync(
        IReadOnlyList<float[]> centroids,
        IReadOnlyList<(long ChunkId, int ClusterIdx)> assignments,
        CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Wipe in dependency order so the FK constraint is satisfied.
        await using (var wipeAssignments = connection.CreateCommand())
        {
            wipeAssignments.Transaction = tx;
            wipeAssignments.CommandText = "DELETE FROM chunk_clusters";
            await wipeAssignments.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await using (var wipeClusters = connection.CreateCommand())
        {
            wipeClusters.Transaction = tx;
            wipeClusters.CommandText = "DELETE FROM clusters";
            await wipeClusters.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var assignedClusterIds = new long[centroids.Count];

        await using (var insertCluster = connection.CreateCommand())
        {
            insertCluster.Transaction = tx;
            insertCluster.CommandText = """
                INSERT INTO clusters(centroid, label, built_at)
                VALUES($c, NULL, $at)
                RETURNING id
                """;
            var cParam = insertCluster.Parameters.Add("$c", SqliteType.Blob);
            insertCluster.Parameters.AddWithValue("$at", nowEpoch);
            for (int i = 0; i < centroids.Count; i++)
            {
                cParam.Value = FloatsToBlob(centroids[i]);
                assignedClusterIds[i] = Convert.ToInt64(await insertCluster.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }
        }

        await using (var insertAssign = connection.CreateCommand())
        {
            insertAssign.Transaction = tx;
            insertAssign.CommandText = "INSERT INTO chunk_clusters(chunk_id, cluster_id) VALUES($cid, $bid)";
            var cidParam = insertAssign.Parameters.Add("$cid", SqliteType.Integer);
            var bidParam = insertAssign.Parameters.Add("$bid", SqliteType.Integer);
            foreach (var (chunkId, idx) in assignments)
            {
                cidParam.Value = chunkId;
                bidParam.Value = assignedClusterIds[idx];
                await insertAssign.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task EnumerateClusterChunkTextsAsync(
        Func<long, string, CancellationToken, Task> onPair,
        CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT cc.cluster_id, c.text
            FROM chunk_clusters cc
            JOIN chunks c ON c.id = cc.chunk_id
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            await onPair(reader.GetInt64(0), reader.GetString(1), ct).ConfigureAwait(false);
        }
    }

    public async Task UpdateClusterLabelsAsync(
        IReadOnlyDictionary<long, string> labels,
        CancellationToken ct = default)
    {
        if (labels.Count == 0) return;
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE clusters SET label = $l WHERE id = $id";
        var lParam = cmd.Parameters.Add("$l", SqliteType.Text);
        var idParam = cmd.Parameters.Add("$id", SqliteType.Integer);
        foreach (var (id, label) in labels)
        {
            lParam.Value = label;
            idParam.Value = id;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClusterRow>> GetClustersAsync(CancellationToken ct = default)
    {
        await EnsureSchemaAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        var rows = new List<ClusterRow>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                c.id,
                c.label,
                c.built_at,
                COUNT(cc.chunk_id) AS n_chunks,
                COUNT(DISTINCT ch.book_id) AS n_books
            FROM clusters c
            LEFT JOIN chunk_clusters cc ON cc.cluster_id = c.id
            LEFT JOIN chunks ch ON ch.id = cc.chunk_id
            GROUP BY c.id, c.label, c.built_at
            ORDER BY n_chunks DESC, c.id
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new ClusterRow(
                Id: reader.GetInt64(0),
                Label: reader.IsDBNull(1) ? null : reader.GetString(1),
                ChunkCount: reader.GetInt32(3),
                BookCount: reader.GetInt32(4),
                BuiltAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2))));
        }
        return rows;
    }

    private static float[] BlobToFloats(byte[] blob)
    {
        var result = new float[blob.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(blob).CopyTo(result);
        return result;
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
            // FK = enforce ON DELETE CASCADE on chunks → embeddings.
            // busy_timeout = block (rather than fail with SQLITE_BUSY) for up to
            // 30 s when another connection holds the write lock — happens during
            // a per-book index transaction colliding with PositionStore writes
            // from page turns.
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000";
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

                    CREATE TABLE IF NOT EXISTS clusters (
                        id        INTEGER PRIMARY KEY,
                        centroid  BLOB NOT NULL,
                        label     TEXT,
                        built_at  INTEGER NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS chunk_clusters (
                        chunk_id   INTEGER PRIMARY KEY REFERENCES chunks(id) ON DELETE CASCADE,
                        cluster_id INTEGER NOT NULL REFERENCES clusters(id) ON DELETE CASCADE
                    );

                    CREATE INDEX IF NOT EXISTS idx_chunk_clusters_cluster ON chunk_clusters(cluster_id);
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
        private readonly EmbeddingStore _store;
        private readonly string _bookPath;
        private readonly List<BufferedChunk> _buffer = new();
        private bool _completed;
        private bool _disposed;

        public IndexSession(EmbeddingStore store, string bookPath)
        {
            _store = store;
            _bookPath = bookPath;
        }

        public Task AppendChunkAsync(int spineIdx, int charOffset, int charLength, string text,
            ReadOnlyMemory<float> embedding, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
                throw new InvalidOperationException("Session has already been completed.");
            _buffer.Add(new BufferedChunk(
                spineIdx, charOffset, charLength, text, embedding.ToArray()));
            return Task.CompletedTask;
        }

        public async Task CompleteAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
                throw new InvalidOperationException("Session has already been completed.");
            await _store.PersistBookAsync(_bookPath, _buffer, ct).ConfigureAwait(false);
            _completed = true;
        }

        public ValueTask DisposeAsync()
        {
            // Buffered data is dropped if not Completed — equivalent to rollback,
            // and free since nothing was ever written.
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
