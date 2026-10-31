using System;
using System.IO;
using System.Text;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Database;

public sealed class SqliteConnectionFactory
{
    private readonly IStorageOptionsService _storageOptionsService;

    public SqliteConnectionFactory(IStorageOptionsService storageOptionsService)
    {
        _storageOptionsService = storageOptionsService;
    }

    public SqliteConnection CreateConnection() => Create(SqliteOpenMode.ReadWriteCreate);

    /// <summary>
    /// Whether the database these connections open is there yet. Before the
    /// first-run "Create library" it is not, and <see cref="CreateConnection"/>
    /// would either fail to open it or create an empty file in its place.
    /// </summary>
    public bool DatabaseExists => File.Exists(_storageOptionsService.Current.DatabasePath);

    /// <summary>
    /// A read-only connection for maintenance probes that must not alter the
    /// database. Read-only rather than read-write is load-bearing: it cannot
    /// create an empty file when the path is wrong, so a missing database fails
    /// loudly instead of quietly reporting a healthy empty one.
    /// </summary>
    public SqliteConnection CreateReadOnlyConnection() => Create(SqliteOpenMode.ReadOnly);

    private SqliteConnection Create(SqliteOpenMode mode)
    {
        var options = _storageOptionsService.Current;
        return Create(
            options.DatabasePath,
            string.IsNullOrWhiteSpace(options.DatabasePassword) ? null : options.DatabasePassword,
            mode,
            foreignKeys: true);
    }

    /// <summary>
    /// The one place a <see cref="SqliteConnection"/> is built (F-28). Every
    /// connection gets the 5-second busy_timeout here, applied on each open
    /// because the pragma is per connection and not persistent. The copies this
    /// replaced drifted - f450d9a's integrity check left the pragma out and
    /// failed with "database is locked" - so ConnectionConstructionGuardTests
    /// fails if a connection or the pragma appears anywhere else.
    /// </summary>
    /// <param name="password">The cipher key; null or empty opens the file unencrypted.</param>
    /// <param name="pooling">
    /// False for connections whose file is renamed, moved or deleted afterwards:
    /// a pooled connection keeps it open after Dispose.
    /// </param>
    /// <param name="foreignKeys">Set when the caller depends on it; null leaves the provider default.</param>
    public static SqliteConnection Create(string path, string? password, SqliteOpenMode mode, bool pooling = true, bool? foreignKeys = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DataSourceFor(path),
            Mode = mode,
            // Private cache (the default) is required: shared cache surfaces
            // in-process write contention as SQLITE_LOCKED, which busy_timeout
            // does NOT retry. Private cache surfaces it as SQLITE_BUSY, which
            // the 5-second busy_timeout below DOES retry.
        };

        if (!pooling)
        {
            builder.Pooling = false;
        }

        if (foreignKeys is { } enforceForeignKeys)
        {
            builder.ForeignKeys = enforceForeignKeys;
        }

        if (!string.IsNullOrEmpty(password))
        {
            builder.Password = password;
        }

        var connection = new SqliteConnection(builder.ToString());
        connection.StateChange += ApplyBusyTimeoutOnOpen;
        return connection;
    }

    /// <summary>
    /// The SQLite3 Multiple Ciphers configuration that keeps the on-disk format
    /// SQLCipher 4 (PBKDF2-HMAC-SHA512 at 256000 iterations, HMAC-SHA512, 4096-byte
    /// pages) - what SQLitePCLRaw.bundle_e_sqlcipher wrote for every installed
    /// library, and what that build can still read back, so a rollback works.
    /// </summary>
    internal const string CipherRecipe = "cipher=sqlcipher&legacy=4";

    /// <summary>
    /// The data source every connection opens: a SQLite URI carrying
    /// <see cref="CipherRecipe"/>. Without it the library falls back to its own
    /// default cipher (sqleet/ChaCha20): an existing library fails to open with
    /// error 26, which reads as a wrong password, and a new one is written in a
    /// format no SQLCipher build can read. Applied with or without a key, so a
    /// plaintext file that is later encrypted with PRAGMA rekey gets the same
    /// scheme. '%', '?', '#' and space are percent-encoded; other characters,
    /// including non-ASCII, pass through as UTF-8.
    /// </summary>
    internal static string DataSourceFor(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || string.Equals(path, ":memory:", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            // A caller-built URI would decide its own cipher parameters.
            throw new ArgumentException("Pass a file path, not a SQLite URI; the factory adds the cipher recipe.", nameof(path));
        }

        var full = Path.GetFullPath(path).Replace('\\', '/');
        var uri = new StringBuilder("file://", full.Length + 40);
        if (!full.StartsWith('/'))
        {
            // C:/x becomes file:///C:/x; a UNC path (//server/share) keeps its slashes.
            uri.Append('/');
        }

        foreach (var ch in full)
        {
            _ = ch switch
            {
                '%' => uri.Append("%25"),
                '?' => uri.Append("%3F"),
                '#' => uri.Append("%23"),
                ' ' => uri.Append("%20"),
                _ => uri.Append(ch),
            };
        }

        return uri.Append('?').Append(CipherRecipe).ToString();
    }

    private static void ApplyBusyTimeoutOnOpen(object? sender, System.Data.StateChangeEventArgs e)
    {
        if (e.CurrentState == System.Data.ConnectionState.Open && sender is SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            // secure_delete (U-C4b, F-19): zero content as it is freed, so a
            // deleted or expired clip does not stay readable in a free page of
            // the file, its backups or a sync client's history. Per connection,
            // like busy_timeout. Measured on the 60k fixture: capture within
            // 0.98-1.06x with it on, plaintext and encrypted. SQLite3 Multiple
            // Ciphers is built with SQLITE_SECURE_DELETE, so on that engine this only
            // matters for a connection that turned it off; it is kept so the guarantee
            // does not depend on a build option (SecureDeleteTests).
            cmd.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA secure_delete = ON;";
            cmd.ExecuteNonQuery();

            // A pooled connection keeps the schema it last read. After another
            // connection changes it - the background payload move drops and builds
            // indexes - SQLite fails the first DELETE on such a connection with
            // "no such table" when the table has an FTS5 delete trigger, foreign
            // keys are on and another table references it (live check F2, where it
            // ended the maintenance loop). An upstream bug, fixed in SQLite 3.54.1
            // (check-in e6b9a40732), so this is needed while the bundled engine is
            // older. Reading sqlite_master checks the schema cookie and reloads a
            // stale schema; on a current one it costs one empty read. When it can
            // be removed: docs/solutions/stale-schema-delete-no-such-table.md.
            // A failure here (a wrong key, a file that is not a database) is left
            // for the caller's first statement to raise, where it always surfaced.
            try
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE 0;";
                cmd.ExecuteScalar();
            }
            catch (SqliteException)
            {
            }
        }
    }
}

