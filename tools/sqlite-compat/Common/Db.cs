using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Uc8;

public sealed class FixtureInfo
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public string? WalFile { get; set; }
    public string Password { get; set; } = "";
    public string Description { get; set; } = "";
    public long Rows { get; set; }
    public long Tags { get; set; }
    public string Checksum { get; set; } = "";
    public Dictionary<string, long> Fts { get; set; } = new();
}

public sealed class Manifest
{
    public string GeneratedBy { get; set; } = "";
    public Dictionary<string, string> Versions { get; set; } = new();
    public Dictionary<string, string> CipherSettings { get; set; } = new();
    public List<FixtureInfo> Fixtures { get; set; } = new();
}

public sealed class RoundTripItem
{
    public string Description { get; set; } = "";
    public string File { get; set; } = "";
    public string Password { get; set; } = "";
    public long Rows { get; set; }
    public long Tags { get; set; }
    public string Checksum { get; set; } = "";
    public Dictionary<string, long> Fts { get; set; } = new();
    public bool ExpectOpenFailure { get; set; }
}

public sealed class CheckResult
{
    public string Id { get; set; } = "";
    public int Row { get; set; }
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed record Snapshot(long Rows, long Tags, string Checksum, Dictionary<string, long> Fts, string Integrity, string FtsIntegrity)
{
    public string Describe() =>
        $"rows={Rows} tags={Tags} checksum={Checksum} integrity={Integrity} fts_integrity={FtsIntegrity} fts=[{string.Join(", ", Fts.Select(kv => $"{kv.Key}:{kv.Value}"))}]";
}

public static class Db
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // App-like schema: AUTOINCREMENT clips, FTS5 trigram external-content table,
    // the three sync triggers (UPDATE scoped like the app's), and a child table
    // with ON DELETE CASCADE.
    public const string SchemaSql = """
        CREATE TABLE clips (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            content      TEXT,
            is_sensitive INTEGER NOT NULL DEFAULT 0
        );
        CREATE VIRTUAL TABLE clips_fts USING fts5(
            content,
            content='clips',
            content_rowid='id',
            tokenize='trigram'
        );
        CREATE TRIGGER clips_ai AFTER INSERT ON clips BEGIN
            INSERT INTO clips_fts(rowid, content) VALUES (new.id, new.content);
        END;
        CREATE TRIGGER clips_ad AFTER DELETE ON clips BEGIN
            INSERT INTO clips_fts(clips_fts, rowid, content) VALUES ('delete', old.id, old.content);
        END;
        CREATE TRIGGER clips_au AFTER UPDATE OF content ON clips BEGIN
            INSERT INTO clips_fts(clips_fts, rowid, content) VALUES ('delete', old.id, old.content);
            INSERT INTO clips_fts(rowid, content) VALUES (new.id, new.content);
        END;
        CREATE TABLE clip_tags (
            id      INTEGER PRIMARY KEY,
            clip_id INTEGER NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
            tag     TEXT NOT NULL
        );
        CREATE INDEX idx_clip_tags_clip ON clip_tags(clip_id);
        CREATE INDEX idx_clips_is_sensitive ON clips(is_sensitive) WHERE is_sensitive = 1;
        """;

    public static readonly string[] Samples =
    [
        "Hello world from clip",
        "שלום עולם, Hebrew text",
        "日本語のテキストです",
        "Ünïcödé façade naïve café",
        "emoji 😀🚀✨ party",
        "Ελληνικά κείμενο",
        "Русский текст для поиска",
        "SELECT * FROM clips WHERE id = 42;",
        "https://example.com/path?q=hello%20world",
        "Tab\tand\nnewline and \"quotes\" 'single'",
    ];

    public static readonly string[] FtsTerms = ["Hello", "עולם", "日本語", "façade", "ünï", "😀🚀✨", "Русский", "example.com", "edited"];

    public static string ContentFor(long i) => $"Clip {i:D5}: {Samples[i % Samples.Length]} #{i * 7919 % 1000}";

    public static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public static string ScalarString(SqliteConnection c, string sql)
    {
        try
        {
            return Convert.ToString(Scalar(c, sql), System.Globalization.CultureInfo.InvariantCulture) ?? "(null)";
        }
        catch (Exception ex)
        {
            return "ERR: " + ex.Message;
        }
    }

    public static List<string> Rows(SqliteConnection c, string sql)
    {
        var list = new List<string>();
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var cols = new List<string>();
                for (int i = 0; i < r.FieldCount; i++)
                {
                    cols.Add(r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!);
                }
                list.Add(string.Join("|", cols));
            }
        }
        catch (Exception ex)
        {
            list.Add("ERR: " + ex.Message);
        }
        return list;
    }

    public static void CreateSchema(SqliteConnection c)
    {
        Exec(c, "PRAGMA journal_mode = WAL;");
        Exec(c, SchemaSql);
    }

    public static void Populate(SqliteConnection c, long from, long count, int batch = 0)
    {
        SqliteTransaction? tx = null;
        using var ins = c.CreateCommand();
        ins.CommandText = "INSERT INTO clips(id, content, is_sensitive) VALUES ($id, $c, $s);";
        var pId = ins.Parameters.Add("$id", SqliteType.Integer);
        var pC = ins.Parameters.Add("$c", SqliteType.Text);
        var pS = ins.Parameters.Add("$s", SqliteType.Integer);
        using var tag = c.CreateCommand();
        tag.CommandText = "INSERT INTO clip_tags(clip_id, tag) VALUES ($id, $t);";
        var tId = tag.Parameters.Add("$id", SqliteType.Integer);
        var tT = tag.Parameters.Add("$t", SqliteType.Text);
        long done = 0;
        for (long i = from; i < from + count; i++)
        {
            if (tx is null)
            {
                tx = c.BeginTransaction();
                ins.Transaction = tx;
                tag.Transaction = tx;
            }
            pId.Value = i;
            pC.Value = ContentFor(i);
            pS.Value = i % 7 == 0 ? 1 : 0;
            ins.ExecuteNonQuery();
            if (i % 2 == 0)
            {
                tId.Value = i;
                tT.Value = $"tag-{i % 5}";
                tag.ExecuteNonQuery();
            }
            done++;
            if (batch > 0 && done % batch == 0)
            {
                tx.Commit();
                tx.Dispose();
                tx = null;
            }
        }
        tx?.Commit();
        tx?.Dispose();
    }

    /// <summary>Update one row (UPDATE trigger) and delete one tagged row (DELETE trigger + FK cascade).</summary>
    public static void Mutate(SqliteConnection c, long updateId, long deleteId)
    {
        Exec(c, $"UPDATE clips SET content = content || ' [edited]' WHERE id = {updateId};");
        Exec(c, $"DELETE FROM clips WHERE id = {deleteId};");
    }

    public static Snapshot Snap(SqliteConnection c, bool checkFtsIntegrity = true)
    {
        long rows = Convert.ToInt64(Scalar(c, "SELECT count(*) FROM clips;"));
        long tags = Convert.ToInt64(Scalar(c, "SELECT count(*) FROM clip_tags;"));
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, content, is_sensitive FROM clips ORDER BY id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                h.AppendData(Encoding.UTF8.GetBytes($"{r.GetInt64(0)}\u001f{(r.IsDBNull(1) ? "<null>" : r.GetString(1))}\u001f{r.GetInt64(2)}\n"));
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, clip_id, tag FROM clip_tags ORDER BY id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                h.AppendData(Encoding.UTF8.GetBytes($"T{r.GetInt64(0)}\u001f{r.GetInt64(1)}\u001f{r.GetString(2)}\n"));
            }
        }
        var checksum = Convert.ToHexString(h.GetHashAndReset())[..32];
        var fts = new Dictionary<string, long>();
        foreach (var term in FtsTerms)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM clips_fts WHERE clips_fts MATCH $q;";
            cmd.Parameters.AddWithValue("$q", "\"" + term.Replace("\"", "\"\"") + "\"");
            fts[term] = Convert.ToInt64(cmd.ExecuteScalar());
        }
        var integrity = string.Join(";", Rows(c, "PRAGMA integrity_check;"));
        string ftsIntegrity = "skipped";
        if (checkFtsIntegrity)
        {
            try
            {
                Exec(c, "INSERT INTO clips_fts(clips_fts, rank) VALUES('integrity-check', 1);");
                ftsIntegrity = "ok";
            }
            catch (Exception ex)
            {
                ftsIntegrity = "ERR: " + ex.Message;
            }
        }
        return new Snapshot(rows, tags, checksum, fts, integrity, ftsIntegrity);
    }

    public static bool SameData(Snapshot a, Snapshot b) =>
        a.Rows == b.Rows && a.Tags == b.Tags && a.Checksum == b.Checksum && a.Fts.OrderBy(k => k.Key).SequenceEqual(b.Fts.OrderBy(k => k.Key));

    public static bool Healthy(Snapshot s) => s.Integrity == "ok" && (s.FtsIntegrity == "ok" || s.FtsIntegrity == "skipped");

    public static string Header(string path)
    {
        var buf = new byte[16];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        int n = fs.Read(buf, 0, 16);
        var ascii = Encoding.ASCII.GetString(buf, 0, n);
        return ascii.StartsWith("SQLite format 3", StringComparison.Ordinal) ? "plaintext" : "encrypted(" + Convert.ToHexString(buf, 0, 8) + "..)";
    }

    public static void CopyShared(string src, string dst)
    {
        using var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var d = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        s.CopyTo(d);
    }

    public static void DeleteDb(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            if (File.Exists(path + suffix))
            {
                File.Delete(path + suffix);
            }
        }
    }

    public static void BusyTimeoutOnOpen(object? sender, StateChangeEventArgs e)
    {
        if (e.CurrentState == ConnectionState.Open && sender is SqliteConnection c)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout = 5000;";
            cmd.ExecuteNonQuery();
        }
    }
}
