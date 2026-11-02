using System.Text;
using Microsoft.Data.Sqlite;

namespace Uc8;

public abstract class Stack
{
    public abstract string Name { get; }

    public abstract string ConnectionString(string path, string? password, SqliteOpenMode mode);

    /// <summary>File name to use in <c>ATTACH ... KEY</c> so the attached file gets the same cipher scheme.</summary>
    public virtual string AttachFileName(string path) => path;

    /// <summary>A <c>VACUUM INTO</c> target that writes an encrypted copy, when the stack supports one.</summary>
    public virtual string? EncryptedVacuumIntoTarget(string path, string password) => null;

    public virtual bool IsMultipleCiphers => false;

    public abstract Dictionary<string, string> Versions(SqliteConnection c);

    public abstract Dictionary<string, string> CipherParams(SqliteConnection c);

    public SqliteConnection Open(string path, string? password, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
        => OpenRaw(ConnectionString(path, password, mode));

    public static SqliteConnection OpenRaw(string connectionString)
    {
        var c = new SqliteConnection(connectionString);
        c.StateChange += Db.BusyTimeoutOnOpen;
        try
        {
            c.Open();
        }
        catch
        {
            c.Dispose();
            throw;
        }
        return c;
    }

    /// <summary>Exactly the shape the app builds today (SqliteConnectionFactory / SqliteDatabaseCopier).</summary>
    public static string AppStyleConnectionString(string dataSource, string? password, SqliteOpenMode mode)
    {
        var b = new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = mode,
            ForeignKeys = true,
            Pooling = false,
        };
        if (!string.IsNullOrEmpty(password))
        {
            b.Password = password;
        }
        return b.ToString();
    }

    protected static void AddCompileOptions(Dictionary<string, string> d, SqliteConnection c)
    {
        var opts = Db.Rows(c, "PRAGMA compile_options;");
        d["compile_options(selected)"] = string.Join(" ", opts.Where(o =>
            o.Contains("FTS", StringComparison.Ordinal) || o.Contains("THREADSAFE", StringComparison.Ordinal) ||
            o.Contains("CODEC", StringComparison.Ordinal) || o.Contains("TEMP_STORE", StringComparison.Ordinal) ||
            o.Contains("SECURE_DELETE", StringComparison.Ordinal) || o.Contains("CIPHER", StringComparison.Ordinal) ||
            o.Contains("LOAD_EXTENSION", StringComparison.Ordinal) || o.Contains("MAX_ATTACHED", StringComparison.Ordinal) ||
            o.Contains("DEFAULT_PAGE_SIZE", StringComparison.Ordinal) || o.Contains("SQLITE3MC", StringComparison.Ordinal) ||
            o.Contains("COMPILER", StringComparison.Ordinal) || o.Contains("CRYPTO", StringComparison.Ordinal) ||
            o.Contains("HAS_", StringComparison.Ordinal) || o.Contains("DQS", StringComparison.Ordinal)));
        d["compile_options(count)"] = opts.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Microsoft.Data.Sqlite.Core + SQLitePCLRaw.bundle_e_sqlcipher (the app's stack).</summary>
public sealed class CurrentStack : Stack
{
    public override string Name => "current: SQLitePCLRaw.bundle_e_sqlcipher 2.1.11";

    public override string ConnectionString(string path, string? password, SqliteOpenMode mode)
        => AppStyleConnectionString(path, password, mode);

    public override Dictionary<string, string> Versions(SqliteConnection c)
    {
        var d = new Dictionary<string, string>
        {
            ["sqlite_version()"] = Db.ScalarString(c, "SELECT sqlite_version();"),
            ["sqlite_source_id()"] = Db.ScalarString(c, "SELECT sqlite_source_id();"),
            ["PRAGMA cipher_version"] = Db.ScalarString(c, "PRAGMA cipher_version;"),
            ["PRAGMA cipher_provider"] = Db.ScalarString(c, "PRAGMA cipher_provider;"),
            ["PRAGMA cipher_provider_version"] = Db.ScalarString(c, "PRAGMA cipher_provider_version;"),
        };
        AddCompileOptions(d, c);
        return d;
    }

    public override Dictionary<string, string> CipherParams(SqliteConnection c)
    {
        var d = new Dictionary<string, string>();
        foreach (var p in new[] { "cipher_version", "kdf_iter", "cipher_page_size", "cipher_kdf_algorithm", "cipher_hmac_algorithm", "cipher_use_hmac", "cipher_plaintext_header_size", "page_size", "journal_mode" })
        {
            d["PRAGMA " + p] = Db.ScalarString(c, $"PRAGMA {p};");
        }
        d["PRAGMA cipher_settings"] = string.Join(" ", Db.Rows(c, "PRAGMA cipher_settings;"));
        d["PRAGMA cipher_default_settings"] = string.Join(" ", Db.Rows(c, "PRAGMA cipher_default_settings;"));
        return d;
    }
}

/// <summary>Any SQLite3 Multiple Ciphers build, configured for the SQLCipher v4 scheme through URI parameters.</summary>
public sealed class Mc3Stack(string name) : Stack
{
    public const string SqlCipherV4Query = "cipher=sqlcipher&legacy=4";

    public override string Name => name;

    public override bool IsMultipleCiphers => true;

    /// <summary>
    /// SQLite URI for an absolute Windows path. '%', '?', '#' and space are
    /// percent-encoded; everything else (including non-ASCII) is passed as UTF-8.
    /// </summary>
    public static string FileUri(string path, string query)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        var sb = new StringBuilder("file:///");
        foreach (var ch in full)
        {
            sb.Append(ch switch
            {
                '%' => "%25",
                '?' => "%3F",
                '#' => "%23",
                ' ' => "%20",
                _ => ch.ToString(),
            });
        }
        return sb + "?" + query;
    }

    public override string ConnectionString(string path, string? password, SqliteOpenMode mode)
        => AppStyleConnectionString(FileUri(path, SqlCipherV4Query), password, mode);

    public override string AttachFileName(string path) => FileUri(path, SqlCipherV4Query);

    public override string? EncryptedVacuumIntoTarget(string path, string password)
        => FileUri(path, SqlCipherV4Query + "&key=" + Uri.EscapeDataString(password));

    public override Dictionary<string, string> Versions(SqliteConnection c)
    {
        var d = new Dictionary<string, string>
        {
            ["sqlite_version()"] = Db.ScalarString(c, "SELECT sqlite_version();"),
            ["sqlite_source_id()"] = Db.ScalarString(c, "SELECT sqlite_source_id();"),
            ["sqlite3mc_version()"] = Db.ScalarString(c, "SELECT sqlite3mc_version();"),
            ["PRAGMA cipher_version (SQLCipher pragma)"] = Db.ScalarString(c, "PRAGMA cipher_version;"),
        };
        AddCompileOptions(d, c);
        return d;
    }

    public override Dictionary<string, string> CipherParams(SqliteConnection c)
    {
        var d = new Dictionary<string, string>();
        foreach (var p in new[] { "cipher", "legacy", "kdf_iter", "fast_kdf_iter", "hmac_use", "hmac_pgno", "hmac_salt_mask", "kdf_algorithm", "hmac_algorithm", "legacy_page_size", "plaintext_header_size", "page_size", "journal_mode" })
        {
            d["PRAGMA " + p] = Db.ScalarString(c, $"PRAGMA {p};");
        }
        foreach (var p in new[] { "cipher", "legacy", "kdf_iter", "kdf_algorithm", "hmac_algorithm" })
        {
            d["PRAGMA temp." + p] = Db.ScalarString(c, $"PRAGMA temp.{p};");
        }
        foreach (var p in new[] { "cipher", "cipher_salt", "raw_key" })
        {
            var v = Db.ScalarString(c, $"SELECT sqlite3mc_codec_data('{p}');");
            d[$"sqlite3mc_codec_data('{p}')"] = p == "raw_key" && !v.StartsWith("ERR", StringComparison.Ordinal) ? $"(redacted, {v.Length} chars)" : v;
        }
        return d;
    }
}
