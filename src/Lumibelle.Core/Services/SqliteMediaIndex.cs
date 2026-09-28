using System.Security.Cryptography;
using System.Text.Json;
using lumibelle.Services.Story;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace lumibelle.Services;

/// <summary>
/// Rebuildable, keyed media metadata. Project JSON remains authoritative: only validated
/// manifests enter the index, and a changed manifest invalidates all its rows together.
/// No image/video bytes, settings secrets, or writable project state live here.
/// </summary>
public sealed class SqliteMediaIndex(ApplicationPaths paths, ILogger<SqliteMediaIndex>? logger = null)
{
    internal string DatabasePath { get; } = Path.Combine(paths.Data, "cache", "media-index-v1.sqlite");
    private readonly ILogger<SqliteMediaIndex> log = logger ?? NullLogger<SqliteMediaIndex>.Instance;
    private static readonly JsonSerializerOptions Json = new(AtomicJsonFile.Options) { WriteIndented = false };
    // A new build must revalidate manifests against that build's domain rules.
    private static readonly string Build = typeof(SqliteMediaIndex).Module.ModuleVersionId.ToString("N");
    private readonly Dictionary<string, (FileVersion File, string Hash)> verified = new(StringComparer.Ordinal);
    private bool unavailable;
    private readonly record struct FileVersion(long Length, long Written, long Created);

    internal async Task<T?> FindAsync<T>(string manifest, string key,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, T>>> readValidated, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested();
        if (unavailable) return (await readValidated(ct)).GetValueOrDefault(key);
        // Shared across store instances. Coalesce a cold gallery's many simultaneous requests
        // into one rebuild and keep SQLite connections/transactions short and private.
        using var gate = await ProjectFiles.LockAsync(DatabasePath, ct);
        manifest = Path.GetFullPath(manifest);
        var file = Version(manifest);
        if (file is null) return (await readValidated(ct)).GetValueOrDefault(key);
        var source = typeof(T).FullName + "|" + manifest;
        if (!verified.TryGetValue(source, out var check) || check.File != file.Value)
        {
            // Verify persistent rows once per file version in each process, including restored
            // files whose timestamps were preserved. Later lookups only stat the manifest.
            try
            {
                await using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                check = (file.Value, Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A source removed/locked between stat and open retains the store's normal
                // missing-document/error behavior; this is not a database failure.
                return (await readValidated(ct)).GetValueOrDefault(key);
            }
            if (Version(manifest) != file) return (await readValidated(ct)).GetValueOrDefault(key);
            verified[source] = check;
        }
        var version = $"{Build}|{check.File.Length}|{check.File.Written}|{check.File.Created}|{check.Hash}";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var connection = Open();
                if (Scalar(connection, "SELECT version FROM sources WHERE source = $source", source) as string == version)
                {
                    using var query = connection.CreateCommand();
                    query.CommandText = "SELECT payload FROM entries WHERE source = $source AND key = $key";
                    query.Parameters.AddWithValue("$source", source);
                    query.Parameters.AddWithValue("$key", key);
                    return query.ExecuteScalar() is string payload
                        ? JsonSerializer.Deserialize<T>(payload, Json) ?? throw new JsonException("Null media index entry.") : null;
                }
                // Source read/validation errors propagate. A cache must never conceal a broken manifest.
                var rows = await readValidated(ct);
                ct.ThrowIfCancellationRequested();
                if (Version(manifest) != file) return (await readValidated(ct)).GetValueOrDefault(key);
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM entries WHERE source = $source; INSERT INTO sources(source, version) VALUES ($source, $version) ON CONFLICT(source) DO UPDATE SET version = excluded.version;";
                command.Parameters.AddWithValue("$source", source);
                command.Parameters.AddWithValue("$version", version);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO entries(source, key, payload) VALUES ($source, $key, $payload)";
                command.Parameters.AddWithValue("$source", source);
                var rowKey = command.Parameters.Add("$key", SqliteType.Text);
                var rowPayload = command.Parameters.Add("$payload", SqliteType.Text);
                command.Prepare();
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    rowKey.Value = row.Key;
                    rowPayload.Value = JsonSerializer.Serialize(row.Value, Json);
                    command.ExecuteNonQuery();
                }
                ct.ThrowIfCancellationRequested();
                transaction.Commit();
                return rows.GetValueOrDefault(key);
            }
            catch (Exception e) when (e is SqliteException { SqliteErrorCode: 11 or 26 } or JsonException)
            {
                // All connections above have closed; these three exact files contain only
                // derived data. Never remove or repair an authoritative project manifest.
                log.LogWarning(e, "Rebuilding the damaged media lookup index.");
                try
                {
                    File.Delete(DatabasePath);
                    File.Delete(DatabasePath + "-wal");
                    File.Delete(DatabasePath + "-shm");
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                { Disable(cleanup); break; }
            }
            catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
            { Disable(e); break; }
        }
        return (await readValidated(ct)).GetValueOrDefault(key);
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 1
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS sources(source TEXT PRIMARY KEY, version TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS entries(source TEXT NOT NULL, key TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(source, key));
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static object? Scalar(SqliteConnection connection, string sql, string source)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$source", source);
        return command.ExecuteScalar();
    }

    private static FileVersion? Version(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks) : null;
    }

    private void Disable(Exception error)
    {
        unavailable = true;
        log.LogWarning(error, "Media lookup index is unavailable; using project manifests until restart.");
    }
}
