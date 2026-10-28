using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Database;
using Clipthrough.Models;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Tests.Benchmarks;

/// <summary>
/// The U-D0 benchmark fixture, promoted from the 2026-10-25 audit (audit-perf).
/// Seeds (once) a realistic heavy-user library on disk and hands out services bound to it.
/// Shape at the default 1 GB library cap: 60k clips, 700 images of 0.4-1.8 MB, text mostly
/// short with a long tail (1% 5-100 KB, 60 clips 0.5-2 MB), source-app icon on 90% of rows.
/// Text clips carry content_bytes = UTF-8(content), as production capture does. Rows are
/// seeded in the pre-U-E1 inline layout and then migrated by the real initializer.
/// </summary>
internal sealed class PerfLibrary
{
    /// <summary>
    /// Where seeded libraries and results.txt live. A seeded 60k library is
    /// about 1 GB, so it is kept between runs and reseeded only when its
    /// ".seeded" marker is missing. Override with CLIPTHROUGH_BENCH_DIR.
    /// </summary>
    public static string Root { get; } =
        Environment.GetEnvironmentVariable("CLIPTHROUGH_BENCH_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "Clipthrough.Bench");

    public PerfLibrary(string dbPath, string password)
    {
        DatabasePath = dbPath;
        StorageOptions = new TestStorageOptionsService(dbPath);
        if (!string.IsNullOrEmpty(password)) StorageOptions.SetInMemoryPassword(password);
        ConnectionFactory = new SqliteConnectionFactory(StorageOptions);
        SensitivityService = new SensitivityService(ConnectionFactory);
        SettingsService = new TestSettingsService();
        Initializer = new DatabaseInitializer(ConnectionFactory, SensitivityService);
        Store = new ClipStoreService(ConnectionFactory, SensitivityService, SettingsService, new TestNotificationService());
    }

    public string DatabasePath { get; }
    public TestStorageOptionsService StorageOptions { get; }
    public SqliteConnectionFactory ConnectionFactory { get; }
    public SensitivityService SensitivityService { get; }
    public TestSettingsService SettingsService { get; }
    public DatabaseInitializer Initializer { get; }
    public ClipStoreService Store { get; }

    public static async Task<PerfLibrary> OpenAsync(int clips, int images, string password = "", string tag = "")
    {
        Directory.CreateDirectory(Root);
        var name = $"lib_{clips}_{images}{(string.IsNullOrEmpty(password) ? "" : "_enc")}{tag}.db";
        var path = Path.Combine(Root, name);
        var marker = path + ".seeded";
        var lib = new PerfLibrary(path, password);
        if (!File.Exists(marker))
        {
            foreach (var f in Directory.GetFiles(Root, name + "*")) File.Delete(f);
            await lib.Initializer.InitializeAsync();
            var sw = Stopwatch.StartNew();
            await SeedAsync(lib, clips, images);
            File.WriteAllText(marker, $"seeded in {sw.ElapsedMilliseconds} ms, of which InitializeAsync from schema 8 (every version-gated step, the U-E1 payload move among them) {MigrationMilliseconds} ms");
        }
        else
        {
            await lib.Initializer.InitializeAsync();
        }
        await lib.SensitivityService.ReloadAsync();
        return lib;
    }

    private static readonly string[] Vocabulary = BuildVocabulary();

    private static string[] BuildVocabulary()
    {
        var rng = new Random(7);
        var words = new List<string>
        {
            "invoice", "meeting", "password", "deploy", "error", "warning", "customer", "report", "https", "github",
            "function", "return", "public", "string", "select", "from", "where", "kusto", "cluster", "table",
        };
        const string letters = "abcdefghijklmnopqrstuvwxyz";
        while (words.Count < 4000)
        {
            var len = rng.Next(3, 11);
            var sb = new StringBuilder(len);
            for (var i = 0; i < len; i++) sb.Append(letters[rng.Next(letters.Length)]);
            words.Add(sb.ToString());
        }
        return words.ToArray();
    }

    public static string MakeText(Random rng, int length)
    {
        var sb = new StringBuilder(length + 16);
        var line = 0;
        while (sb.Length < length)
        {
            sb.Append(Vocabulary[rng.Next(Vocabulary.Length)]);
            if (++line % 12 == 0) sb.Append('\n'); else sb.Append(' ');
        }
        return sb.ToString(0, length);
    }

    public static string MakeLog(Random rng, int length)
    {
        var sb = new StringBuilder(length + 200);
        var t = new DateTime(2025, 3, 1, 8, 0, 0);
        var i = 0;
        while (sb.Length < length)
        {
            t = t.AddMilliseconds(rng.Next(1, 900));
            sb.Append(t.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [")
              .Append(i % 9 == 0 ? "WARN" : "INFO").Append("] worker-").Append(rng.Next(32))
              .Append(' ').Append(Vocabulary[rng.Next(Vocabulary.Length)]).Append(' ')
              .Append(Vocabulary[rng.Next(Vocabulary.Length)]).Append(" id=").Append(rng.Next(1_000_000)).Append('\n');
            i++;
        }
        return sb.ToString(0, length);
    }

    private static async Task SeedAsync(PerfLibrary lib, int total, int images)
    {
        var rng = new Random(42);
        var baseTime = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var icon = new byte[3000];
        rng.NextBytes(icon);
        var imageBuffer = new byte[2 * 1024 * 1024];
        rng.NextBytes(imageBuffer);

        var imageEvery = images > 0 ? total / images : int.MaxValue;

        await using var connection = lib.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        const int batch = 2000;
        for (var start = 0; start < total; start += batch)
        {
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO clips (content, content_bytes, content_type, content_format, source_app, source_app_path,
                    source_app_icon, hash, is_favorite, is_sensitive, is_pasted, copy_count, paste_count,
                    first_copied_at, last_copied_at, captured_at, byte_size, image_width, image_height,
                    source_window_title, ocr_text, ocr_status, sensitivity_scanned_at, embedding_status)
                VALUES ($c, $cb, $ct, $cf, $app, 'C:\Program Files\App\app.exe', $icon, $h, $fav, 0, 0, 1, $paste,
                    $t, $t, $t, $b, $w, $hgt, $title, $ocr, $ocrs, $t, $es);
                """;
            var pc = command.Parameters.Add("$c", SqliteType.Text);
            var pcb = command.Parameters.Add("$cb", SqliteType.Blob);
            var pct = command.Parameters.Add("$ct", SqliteType.Text);
            var pcf = command.Parameters.Add("$cf", SqliteType.Text);
            var papp = command.Parameters.Add("$app", SqliteType.Text);
            var picon = command.Parameters.Add("$icon", SqliteType.Blob);
            var ph = command.Parameters.Add("$h", SqliteType.Text);
            var pfav = command.Parameters.Add("$fav", SqliteType.Integer);
            var ppaste = command.Parameters.Add("$paste", SqliteType.Integer);
            var pt = command.Parameters.Add("$t", SqliteType.Text);
            var pb = command.Parameters.Add("$b", SqliteType.Integer);
            var pw = command.Parameters.Add("$w", SqliteType.Integer);
            var phg = command.Parameters.Add("$hgt", SqliteType.Integer);
            var ptitle = command.Parameters.Add("$title", SqliteType.Text);
            var pocr = command.Parameters.Add("$ocr", SqliteType.Text);
            var pocrs = command.Parameters.Add("$ocrs", SqliteType.Text);
            var pes = command.Parameters.Add("$es", SqliteType.Text);

            for (var i = start; i < Math.Min(total, start + batch); i++)
            {
                var isImage = images > 0 && i % imageEvery == imageEvery / 2;
                string content;
                byte[] bytes;
                if (isImage)
                {
                    var size = rng.Next(400 * 1024, 1800 * 1024);
                    bytes = imageBuffer.AsSpan(rng.Next(0, imageBuffer.Length - size), size).ToArray();
                    bytes[0] = (byte)i; bytes[1] = (byte)(i >> 8); bytes[2] = (byte)(i >> 16);
                    content = "Image 1920 x 1080";
                    pct.Value = "image"; pcf.Value = "bitmap"; pw.Value = 1920; phg.Value = 1080;
                    pocr.Value = MakeText(rng, 300); pocrs.Value = "succeeded";
                }
                else
                {
                    var roll = rng.NextDouble();
                    int len = roll < 0.85 ? rng.Next(20, 300)
                        : roll < 0.99 ? rng.Next(300, 5000)
                        : rng.Next(5000, 100_000);
                    if (i % (total / 60) == 7) len = rng.Next(512 * 1024, 2 * 1024 * 1024 - 1024);
                    content = len > 100_000 ? MakeLog(rng, len) : MakeText(rng, len);
                    bytes = Encoding.UTF8.GetBytes(content);
                    pct.Value = "text"; pcf.Value = "text"; pw.Value = DBNull.Value; phg.Value = DBNull.Value;
                    pocr.Value = DBNull.Value; pocrs.Value = DBNull.Value;
                }

                pc.Value = content;
                pcb.Value = bytes;
                papp.Value = $"app{rng.Next(25)}.exe";
                picon.Value = rng.NextDouble() < 0.9 ? icon : DBNull.Value;
                ph.Value = $"h{i}";
                pfav.Value = i % 97 == 0 ? 1 : 0;
                ppaste.Value = rng.Next(4);
                pt.Value = baseTime.AddMinutes(i * 5).ToString("O");
                pb.Value = bytes.LongLength;
                ptitle.Value = $"Window {Vocabulary[rng.Next(Vocabulary.Length)]} - App";
                pes.Value = "succeeded";
                await command.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }

        // The rows above are written in the layout every installed library has
        // before U-E1 - payload and icon inline - and then migrated by the real
        // code, so the fixture is what an upgraded library looks like and the
        // seed log carries the migration's cost at this size.
        await using (var rewind = connection.CreateCommand())
        {
            rewind.CommandText = "UPDATE app_metadata SET value = '8' WHERE key = 'schema_version';";
            await rewind.ExecuteNonQueryAsync();
        }

        var migration = Stopwatch.StartNew();
        await lib.Initializer.InitializeAsync();
        MigrationMilliseconds = migration.ElapsedMilliseconds;

        await using var cp = connection.CreateCommand();
        cp.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await cp.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// How long InitializeAsync took on the last seed after rewinding to schema 8, or -1.
    /// That runs every version-gated step - the integrity check and the full FTS
    /// rebuild among them - not just the U-E1 payload move; time the move alone with P16.
    /// </summary>
    public static long MigrationMilliseconds { get; private set; } = -1;
}
