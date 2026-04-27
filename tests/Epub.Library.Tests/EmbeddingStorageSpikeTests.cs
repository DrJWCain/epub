using System.Numerics;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Epub.Library.Tests;

/// <summary>
/// P0 spike for the semantic-search milestone: prove that storing 32-bit float
/// embeddings as raw SQLite BLOBs and ranking them with a managed cosine scan
/// works on this platform. Stands in for the originally planned sqlite-vec spike,
/// which was abandoned after sqlite-vec shipped no win-arm64 prebuilt.
/// </summary>
public sealed class EmbeddingStorageSpikeTests
{
    [Fact]
    public async Task Roundtrip_BlobFloats_PreservesValuesExactly()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE embeddings (id INTEGER PRIMARY KEY, vec BLOB NOT NULL)";
            await create.ExecuteNonQueryAsync();
        }

        var original = new float[] { 1.0f, -2.5f, 3.14159f, float.Epsilon, -0.0f, 1e-30f, 1e30f, 0.5f };

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO embeddings(id, vec) VALUES (1, $vec)";
            insert.Parameters.AddWithValue("$vec", FloatsToBlob(original));
            await insert.ExecuteNonQueryAsync();
        }

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT vec FROM embeddings WHERE id = 1";
        await using var reader = await select.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        var blob = (byte[])reader["vec"];
        var decoded = BlobToFloats(blob);

        decoded.Should().Equal(original, "BLOB round-trip must preserve every float bit-for-bit");
    }

    [Fact]
    public async Task TopK_CosineRanking_AgreesWithHandComputed()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE embeddings (id INTEGER PRIMARY KEY, vec BLOB NOT NULL)";
            await create.ExecuteNonQueryAsync();
        }

        var corpus = new (long Id, float[] Vec)[]
        {
            (1, Normalize(new float[] { 1, 0, 0, 0, 0, 0, 0, 0 })),
            (2, Normalize(new float[] { 0.9f, 0.1f, 0, 0, 0, 0, 0, 0 })),
            (3, Normalize(new float[] { 0, 1, 0, 0, 0, 0, 0, 0 })),
            (4, Normalize(new float[] { -1, 0, 0, 0, 0, 0, 0, 0 })),
        };

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO embeddings(id, vec) VALUES ($id, $vec)";
            var idParam = insert.Parameters.Add("$id", SqliteType.Integer);
            var vecParam = insert.Parameters.Add("$vec", SqliteType.Blob);
            foreach (var (id, vec) in corpus)
            {
                idParam.Value = id;
                vecParam.Value = FloatsToBlob(vec);
                await insert.ExecuteNonQueryAsync();
            }
        }

        var query = Normalize(new float[] { 1, 0, 0, 0, 0, 0, 0, 0 });

        var ranked = await BruteForceTopK(connection, query, k: 4);

        ranked.Select(r => r.Id).Should().Equal(new long[] { 1, 2, 3, 4 },
            "ids ordered by descending cosine similarity to the query [1,0,0,0,0,0,0,0]: " +
            "id=1 is identical, id=2 is close, id=3 is orthogonal, id=4 is opposite");

        ranked[0].Similarity.Should().BeApproximately(1.0f, 1e-5f);
        ranked[1].Similarity.Should().BeGreaterThan(0.9f).And.BeLessThan(1.0f);
        ranked[2].Similarity.Should().BeApproximately(0.0f, 1e-5f);
        ranked[3].Similarity.Should().BeApproximately(-1.0f, 1e-5f);
    }

    [Fact]
    public async Task BruteForce_OverThousandVectors_StaysFast()
    {
        const int corpusSize = 10_000;
        const int dim = 384;
        var rng = new Random(42);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE embeddings (id INTEGER PRIMARY KEY, vec BLOB NOT NULL)";
            await create.ExecuteNonQueryAsync();
        }

        await using (var tx = (SqliteTransaction)await connection.BeginTransactionAsync())
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO embeddings(id, vec) VALUES ($id, $vec)";
            var idParam = insert.Parameters.Add("$id", SqliteType.Integer);
            var vecParam = insert.Parameters.Add("$vec", SqliteType.Blob);
            for (long i = 0; i < corpusSize; i++)
            {
                idParam.Value = i;
                vecParam.Value = FloatsToBlob(Normalize(RandomVec(rng, dim)));
                await insert.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        var query = Normalize(RandomVec(rng, dim));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ranked = await BruteForceTopK(connection, query, k: 10);
        sw.Stop();

        ranked.Should().HaveCount(10);
        ranked[0].Similarity.Should().BeGreaterThan(ranked[9].Similarity);
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            $"brute-force scan over {corpusSize} {dim}-dim vectors should be well under half a second");
    }

    private static byte[] FloatsToBlob(float[] floats)
        => MemoryMarshal.AsBytes(floats.AsSpan()).ToArray();

    private static float[] BlobToFloats(byte[] blob)
    {
        var result = new float[blob.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(blob).CopyTo(result);
        return result;
    }

    private static float[] Normalize(float[] v)
    {
        float sumSq = 0;
        foreach (var x in v) sumSq += x * x;
        var norm = MathF.Sqrt(sumSq);
        if (norm == 0) return v;
        var result = new float[v.Length];
        for (int i = 0; i < v.Length; i++) result[i] = v[i] / norm;
        return result;
    }

    private static float[] RandomVec(Random rng, int dim)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++) v[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        return v;
    }

    private static float CosineSim(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vector dimension mismatch.");

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

    private static async Task<IReadOnlyList<(long Id, float Similarity)>> BruteForceTopK(
        SqliteConnection connection, float[] query, int k)
    {
        var results = new List<(long Id, float Similarity)>();

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, vec FROM embeddings";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt64(0);
            var blob = (byte[])reader["vec"];
            var sim = CosineSim(query, MemoryMarshal.Cast<byte, float>(blob));
            results.Add((id, sim));
        }

        return results.OrderByDescending(r => r.Similarity).Take(k).ToList();
    }
}
