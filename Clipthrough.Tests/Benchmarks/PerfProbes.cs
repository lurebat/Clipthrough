using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Presentation;
using Clipthrough.Services;
using Clipthrough.Services.Search;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Benchmarks;

/// <summary>
/// Opt-in benchmarks over <see cref="PerfLibrary"/> (U-D0). Skipped unless
/// CLIPTHROUGH_BENCH=1, so neither the default run nor CI pays for seeding a
/// 1 GB library. Results are appended to <c>results.txt</c> under
/// <see cref="PerfLibrary.Root"/>.
///
/// Knobs: CLIPTHROUGH_BENCH_CLIPS (default 60000), CLIPTHROUGH_BENCH_IMAGES
/// (700), CLIPTHROUGH_BENCH_PASSWORD (empty = unencrypted; set it for the
/// encrypted variant). Run on a quiet machine; numbers from a loaded one are
/// not comparable.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class PerfProbes
{
    private static readonly string ResultsPath = Path.Combine(PerfLibrary.Root, "results.txt");
    private static readonly object WriteLock = new();

    private static int Clips => int.TryParse(Environment.GetEnvironmentVariable("CLIPTHROUGH_BENCH_CLIPS"), out var v) ? v : 60_000;
    private static int Images => int.TryParse(Environment.GetEnvironmentVariable("CLIPTHROUGH_BENCH_IMAGES"), out var v) ? v : 700;
    private static string Password => Environment.GetEnvironmentVariable("CLIPTHROUGH_BENCH_PASSWORD") ?? string.Empty;

    private static void Log(string line)
    {
        lock (WriteLock)
        {
            File.AppendAllText(ResultsPath, line + Environment.NewLine);
        }
        Console.WriteLine(line);
    }

    private static async Task<T> Measure<T>(string label, Func<Task<T>> body, int runs = 5)
    {
        var result = await body(); // warm-up
        var times = new List<double>();
        long alloc = 0;
        for (var i = 0; i < runs; i++)
        {
            var a0 = GC.GetTotalAllocatedBytes(true);
            var sw = Stopwatch.StartNew();
            result = await body();
            sw.Stop();
            alloc = GC.GetTotalAllocatedBytes(true) - a0;
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        Log($"{label,-70} median={times[times.Count / 2],9:F1} ms  min={times[0],9:F1}  max={times[^1],9:F1}  alloc(last)={alloc / 1024.0 / 1024.0,8:F1} MB");
        return result;
    }

    private static Task Measure(string label, Func<Task> body, int runs = 5)
        => Measure<int>(label, async () => { await body(); return 0; }, runs);

    private static async Task<long> ScalarAsync(PerfLibrary lib, string sql)
    {
        await using var c = lib.ConnectionFactory.CreateConnection();
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<int> CountRowsAsync(PerfLibrary lib, string sql)
    {
        await using var c = lib.ConnectionFactory.CreateConnection();
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();
        var n = 0;
        while (await r.ReadAsync()) n++;
        return n;
    }

    private static string Header(PerfLibrary lib)
    {
        var size = new FileInfo(lib.DatabasePath).Length / 1024.0 / 1024.0;
        return $"=== {DateTime.Now:HH:mm:ss} db={Path.GetFileName(lib.DatabasePath)} size={size:F0} MB enc={(Password.Length > 0)} clips={Clips} images={Images}";
    }

    [BenchmarkFact]
    public async Task P1_Search()
    {
        var lib = await PerfLibrary.OpenAsync(Clips, Images, Password);
        Log(Header(lib) + " :: P1 search (Limit=200, offset 0)");
        var store = lib.Store;

        async Task Run(string label, ClipSearchFilters f)
        {
            var r = await Measure(label, () => store.SearchAsync(f));
            Log($"    -> items={r.Items.Count} total={r.TotalMatchingCount} contentChars={r.Items.Sum(i => (long)i.ContentPrefix.Length)} truncated={r.Items.Count(i => i.IsContentTruncated)}");
        }

        await Run("blank MostRecent", new ClipSearchFilters());
        await Run("blank Alphabetical", new ClipSearchFilters { SortOption = ClipSortOption.Alphabetical });
        await Run("blank Image filter", new ClipSearchFilters { ContentTypes = [ContentType.Image] });
        await Run("blank Text filter", new ClipSearchFilters { ContentTypes = [ContentType.Text] });
        await Run("blank Image filter Alphabetical", new ClipSearchFilters { ContentTypes = [ContentType.Image], SortOption = ClipSortOption.Alphabetical });
        await Run("blank Text filter Alphabetical", new ClipSearchFilters { ContentTypes = [ContentType.Text], SortOption = ClipSortOption.Alphabetical });
        await Run("blank Text filter MostPasted", new ClipSearchFilters { ContentTypes = [ContentType.Text], SortOption = ClipSortOption.MostPasted });
        await Run("blank Text filter LargestFirst", new ClipSearchFilters { ContentTypes = [ContentType.Text], SortOption = ClipSortOption.LargestFirst });
        await Run("blank Text filter OldestFirst", new ClipSearchFilters { ContentTypes = [ContentType.Text], SortOption = ClipSortOption.OldestFirst });
        await Run("FTS common 'invoice'", new ClipSearchFilters { SearchText = "invoice" });
        await Run("FTS 2 words 'invoice meeting'", new ClipSearchFilters { SearchText = "invoice meeting" });
        await Run("FTS no-match 'qqzzxx'", new ClipSearchFilters { SearchText = "qqzzxx" });
        await Run("FTS fuzzy 'invoce'", new ClipSearchFilters { SearchText = "invoce", UseFuzzy = true });
        await Run("in-memory 2-char no-match 'qz'", new ClipSearchFilters { SearchText = "qz" });
        await Run("in-memory 2-char common 'ab'", new ClipSearchFilters { SearchText = "ab" });
        await Run("in-memory 'invoice qz' (mixed short token)", new ClipSearchFilters { SearchText = "invoice qz" });
        await Run("regex no-match 'worker-99 id=1x'", new ClipSearchFilters { SearchText = "worker-99 id=1x", UseRegex = true });
        await Run("case-sensitive no-match 'Invoice'", new ClipSearchFilters { SearchText = "Invoice", CaseSensitive = true });
        await Run("whole-word 'invoice'", new ClipSearchFilters { SearchText = "invoice", WholeWord = true });
        await Run("whole-word no-match 'qqzzxx'", new ClipSearchFilters { SearchText = "qqzzxx", WholeWord = true });
        await Run("deep refresh blank MostRecent Limit=2000", new ClipSearchFilters { Limit = 2000 });
    }

    [BenchmarkFact]
    public async Task P7_Explain()
    {
        var lib = await PerfLibrary.OpenAsync(Clips, Images, Password);
        Log(Header(lib) + " :: P7 plans");
        await using var c = lib.ConnectionFactory.CreateConnection();
        await c.OpenAsync();
        async Task Plan(string label, string sql, Action<SqliteCommand> bind)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            bind(cmd);
            await using var r = await cmd.ExecuteReaderAsync();
            Log($"  plan[{label}]:");
            while (await r.ReadAsync()) Log($"      {r.GetString(3)}");
        }
        var typeFilter = new ClipSearchFilters { ContentTypes = [ContentType.Image] };
        await Plan("blank + content type", ClipStoreService.BuildSearchSql(typeFilter, false, false), cmd =>
        {
            cmd.Parameters.AddWithValue("$contentType0", "image");
            cmd.Parameters.AddWithValue("$limit", 201);
            cmd.Parameters.AddWithValue("$offset", 0);
        });
        var fts = new ClipSearchFilters { SearchText = "invoice" };
        await Plan("fts invoice narrow plan", ClipStoreService.BuildSearchSql(fts, true, false), cmd =>
        {
            cmd.Parameters.AddWithValue("$search", "\"invoice\"");
            cmd.Parameters.AddWithValue("$limit", 201);
            cmd.Parameters.AddWithValue("$offset", 0);
        });
        await Plan("blank alphabetical", ClipStoreService.BuildSearchSql(new ClipSearchFilters { SortOption = ClipSortOption.Alphabetical }, false, false), cmd =>
        {
            cmd.Parameters.AddWithValue("$limit", 201);
            cmd.Parameters.AddWithValue("$offset", 0);
        });
        Log($"  matches 'invoice' = {await ScalarAsync(lib, "SELECT COUNT(*) FROM clips_fts WHERE clips_fts MATCH '\"invoice\"';")}");
        Log($"  matches 'invoice' AND 'meeting' = {await ScalarAsync(lib, "SELECT COUNT(*) FROM clips_fts WHERE clips_fts MATCH '\"invoice\" AND \"meeting\"';")}");
        Log($"  text rows = {await ScalarAsync(lib, "SELECT COUNT(*) FROM clips WHERE content_type='text';")}");
        Log($"  sum stored_bytes MB = {await ScalarAsync(lib, "SELECT SUM(stored_bytes)/1048576 FROM clips;")}  sum length(content_bytes) MB = {await ScalarAsync(lib, "SELECT SUM(length(content_bytes))/1048576 FROM clip_payloads;")}  sum icon MB = {await ScalarAsync(lib, "SELECT COALESCE(SUM(length(png)), 0)/1048576 FROM app_icons;")}");
        Log($"  page_size={await ScalarAsync(lib, "PRAGMA page_size;")} cache_size={await ScalarAsync(lib, "PRAGMA cache_size;")} auto_vacuum={await ScalarAsync(lib, "PRAGMA auto_vacuum;")}");
        const string elig = """
            is_sensitive = 0
            AND sensitivity_scanned_at IS NOT NULL
            AND (
                (content_type IN ('text','richtext','files') AND content IS NOT NULL AND TRIM(content) <> '')
                OR (content_type = 'image' AND ocr_status = 'succeeded' AND ocr_text IS NOT NULL AND TRIM(ocr_text) <> '')
            )
            """;
        await Plan("embedding claim (copied from ClaimPendingEmbeddingsCoreAsync)", $"""
            SELECT id, (CASE WHEN content_type = 'image' THEN ocr_text ELSE content END) AS etext
            FROM clips
            WHERE (embedding_status IS NULL OR embedding_status IN ('pending','rerun')
                   OR (embedding_status = 'failed' AND embedding_attempts < $maxAttempts))
              AND {elig}
            ORDER BY COALESCE(last_copied_at, captured_at) DESC
            LIMIT $limit;
            """, cmd => { cmd.Parameters.AddWithValue("$limit", 32); cmd.Parameters.AddWithValue("$maxAttempts", 3); });
        await Plan("ocr coverage", "SELECT COUNT(*), SUM(CASE WHEN ocr_status = 'succeeded' THEN 1 ELSE 0 END) FROM clips WHERE content_type = 'image' AND EXISTS (SELECT 1 FROM clip_payloads p WHERE p.clip_id = clips.id);", _ => { });
        await Plan("pending sensitivity", "SELECT id FROM clips WHERE sensitivity_scanned_at IS NULL ORDER BY id;", _ => { });
        await Plan("retention lifetime delete probe", "SELECT id FROM clips WHERE is_sensitive = 0 AND last_copied_at < '2020' AND NOT (is_favorite = 1 OR pinned_at IS NOT NULL);", _ => { });
    }

    [BenchmarkFact]
    public async Task P11_ClaimDuringBackfill()
    {
        var lib = await PerfLibrary.OpenAsync(Clips, Images, Password);
        Log(Header(lib) + " :: P11 embedding claim with a real backlog (all clips pending), restored afterwards");
        async Task Exec(string sql)
        {
            await using var c = lib.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        try
        {
            await Exec("UPDATE clips SET embedding_status = NULL;");
            await Measure("ClaimPendingEmbeddingsAsync(32) with 60k pending (+release)", async () =>
            {
                var claimed = await lib.Store.ClaimPendingEmbeddingsAsync(32);
                await lib.Store.ReleaseEmbeddingClaimsAsync(claimed.Select(c => c.ClipId).ToList());
                return claimed.Count;
            });
            await Measure("GetEmbeddingCoverageAsync with 60k pending", () => lib.Store.GetEmbeddingCoverageAsync(), runs: 3);
        }
        finally
        {
            await Exec("UPDATE clips SET embedding_status = 'succeeded';");
        }
    }

    [BenchmarkFact]
    public async Task P12_RerunAllHoldsWriteLock()
    {
        var lib = await PerfLibrary.OpenAsync(Clips, Images, Password);
        Log(Header(lib) + " :: P12 MarkAllEmbeddingsForRerunAsync vs a concurrent capture");
        var sw = Stopwatch.StartNew();
        var rerun = lib.Store.MarkAllEmbeddingsForRerunAsync();
        await Task.Delay(1000);
        var captureSw = Stopwatch.StartNew();
        string outcome;
        try
        {
            var t = "probe concurrent capture " + Guid.NewGuid();
            var clip = await lib.Store.CaptureFastAsync(new ClipCaptureRequest { ContentText = t, ContentBytes = Encoding.UTF8.GetBytes(t), SourceApp = "probe.exe" });
            outcome = $"capture succeeded id={clip?.Id}";
        }
        catch (Exception ex)
        {
            outcome = $"capture FAILED: {ex.GetType().Name}: {ex.Message}";
        }
        Log($"  concurrent capture after {captureSw.ElapsedMilliseconds} ms -> {outcome}");
        var ids = await rerun;
        Log($"  MarkAllEmbeddingsForRerunAsync rows={ids.Count} took {sw.ElapsedMilliseconds} ms");
        await using var c = lib.ConnectionFactory.CreateConnection();
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE clips SET embedding_status = 'succeeded';";
        await cmd.ExecuteNonQueryAsync();
    }

    [BenchmarkFact]
    public async Task P13_RebuildSensitivity()
    {
        var lib = await PerfLibrary.OpenAsync(Clips, Images, Password);
        Log(Header(lib) + " :: P13 RebuildSensitivityMatchesAsync (runs on every sensitivity-rule save)");
        await Measure("RebuildSensitivityMatchesAsync", () => lib.Store.RebuildSensitivityMatchesAsync(), runs: 1);
    }

    [BenchmarkFact]
    public async Task P14_CaptureDuringClaim()
    {
        var lib = await PerfLibrary.OpenAsync(Clips, Images, Password);
        Log(Header(lib) + " :: P14 capture latency while an (empty) embedding claim runs");
        var lat = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var claim = lib.Store.ClaimPendingEmbeddingsAsync(32);
            await Task.Delay(100);
            var sw = Stopwatch.StartNew();
            var t = "probe capture during claim " + Guid.NewGuid();
            await lib.Store.CaptureFastAsync(new ClipCaptureRequest { ContentText = t, ContentBytes = Encoding.UTF8.GetBytes(t), SourceApp = "probe.exe" });
            lat.Add(sw.ElapsedMilliseconds);
            await claim;
        }
        lat.Sort();
        Log($"  small-clip CaptureFastAsync started 100 ms into a claim: median={lat[2]} ms min={lat[0]} max={lat[4]} (baseline small capture ~3 ms, P4)");
    }

    [BenchmarkFact]
    public async Task P2_OverflowChain()
    {
        var lib = await PerfLibrary.OpenAsync(Clips, Images, Password);
        Log(Header(lib) + " :: P2 overflow chain (column position vs. blob)");
        // Columns before content_bytes: id, content. Everything else is stored after it.
        await Measure("scan images: SELECT id (rowid only) WHERE content_type idx", () => CountRowsAsync(lib, "SELECT id FROM clips INDEXED BY idx_clips_content_type WHERE content_type = 'image';"));
        await Measure("scan images: SELECT id, content (col 1, before blob)", () => CountRowsAsync(lib, "SELECT id, typeof(content) FROM clips INDEXED BY idx_clips_content_type WHERE content_type = 'image';"));
        await Measure("scan images: SELECT ocr_status (col after blob)", () => CountRowsAsync(lib, "SELECT ocr_status FROM clips INDEXED BY idx_clips_content_type WHERE content_type = 'image';"));
        await Measure("full scan: typeof(content) IS NULL (col 1)", () => ScalarAsync(lib, "SELECT COUNT(*) FROM clips WHERE typeof(content) = 'null';"));
        await Measure("full scan: sensitivity_scanned_at IS NULL (startup query)", () => ScalarAsync(lib, "SELECT COUNT(*) FROM clips WHERE sensitivity_scanned_at IS NULL;"));
        await Measure("page 200 images ClipList cols (via SearchAsync image filter)", () => lib.Store.SearchAsync(new ClipSearchFilters { ContentTypes = [ContentType.Image] }));
    }

    [BenchmarkFact]
    public async Task P3_StartupAndCoverage()
    {
        var lib = await PerfLibrary.OpenAsync(Clips, Images, Password);
        Log(Header(lib) + " :: P3 startup / coverage queries");
        var store = lib.Store;
        await Measure("DatabaseInitializer.InitializeAsync (already migrated)", () => lib.Initializer.InitializeAsync(), runs: 3);
        await Measure("PrewarmAsync", () => store.PrewarmAsync());
        await Measure("ApplyPendingSensitivityAsync (0 pending)", () => store.ApplyPendingSensitivityAsync());
        await Measure("GetPendingOcrClipIdsAsync", () => store.GetPendingOcrClipIdsAsync());
        await Measure("GetOcrCoverageAsync", () => store.GetOcrCoverageAsync());
        await Measure("GetEmbeddingCoverageAsync", () => store.GetEmbeddingCoverageAsync());
        await Measure("ClaimPendingEmbeddingsAsync(32) (none pending)", () => store.ClaimPendingEmbeddingsAsync(32));
        // Stats recompute: every capture invalidates, the next search recomputes.
        await Measure("SearchAsync blank after stats invalidation", async () =>
        {
            await store.SetFavoriteAsync(1, false);
            return await store.SearchAsync(new ClipSearchFilters());
        });
        await Measure("stats: SUM(stored_bytes)", () => ScalarAsync(lib, "SELECT COALESCE(SUM(stored_bytes),0) FROM clips;"));
        await Measure("stats: COUNT(*)", () => ScalarAsync(lib, "SELECT COUNT(*) FROM clips;"));
    }

    [BenchmarkFact]
    public async Task P4_Capture()
    {
        var lib = await PerfLibrary.OpenAsync(Clips, Images, Password);
        Log(Header(lib) + " :: P4 capture path");
        var store = lib.Store;
        lib.SettingsService.SetCurrent(AppSettings.Default with { MaxLibrarySizeMegabytes = 4096 });
        var rng = new Random(Environment.TickCount);

        foreach (var size in new[] { 200, 100_000, 1_900_000 })
        {
            var text = PerfLibrary.MakeLog(rng, size);
            var html = "<html><body><pre>" + System.Net.WebUtility.HtmlEncode(text) + "</pre></body></html>";
            long id = 0;
            await Measure($"CaptureFastAsync new text {size / 1024} KB", async () =>
            {
                var t = text + Guid.NewGuid().ToString("N");
                var clip = await store.CaptureFastAsync(new ClipCaptureRequest { ContentText = t, ContentBytes = Encoding.UTF8.GetBytes(t), SourceApp = "probe.exe" });
                id = clip!.Id;
            });
            await Measure($"ApplySensitivityAsync {size / 1024} KB", () => store.ApplySensitivityAsync(id));
            await Measure($"UpdateDeferredContentAsync (html, same text) {size / 1024} KB", () => store.UpdateDeferredContentAsync(id, new ClipCaptureRequest
            {
                ContentText = text,
                ContentBytes = Encoding.UTF8.GetBytes(html),
                ContentType = ContentType.RichText,
                ContentFormat = ClipContentFormat.Html,
            }));
            var dupBytes = Encoding.UTF8.GetBytes(text + "dup");
            await Measure($"CaptureFastAsync duplicate (re-copy) {size / 1024} KB", () => store.CaptureFastAsync(new ClipCaptureRequest { ContentText = text + "dup", ContentBytes = dupBytes, SourceApp = "probe.exe" }));
            await Measure($"UpdateSourceAppIconAsync {size / 1024} KB", () => store.UpdateSourceAppIconAsync(id, new byte[3000]));
            await Measure($"SetFavoriteAsync (metadata-only update) {size / 1024} KB", () => store.SetFavoriteAsync(id, false));
            var sens = await Measure($"SensitivityService.Scan only {size / 1024} KB", () => Task.FromResult(lib.SensitivityService.Scan(text)));
        }

        await Measure("ApplyMaintenanceAsync (no purge, default caps but 4 GB)", () => store.ApplyMaintenanceAsync());
    }

    [BenchmarkFact]
    public async Task P5_Formatter()
    {
        Log($"=== {DateTime.Now:HH:mm:ss} :: P5 ClipDisplayFormatter.BuildDisplayStrings (row VM ctor work)");
        var rng = new Random(3);
        foreach (var size in new[] { 300, 100_000, 1_900_000 })
        {
            var text = PerfLibrary.MakeLog(rng, size);
            var plain = new ClipEntry { Id = 1, Content = text, ContentType = ContentType.Text, Hash = "h" };
            await Measure($"BuildDisplayStrings text {size / 1024} KB", () => Task.FromResult(ClipDisplayFormatter.BuildDisplayStrings(plain)), runs: 7);
            var rich = new ClipEntry { Id = 2, Content = text, ContentType = ContentType.RichText, ContentFormat = ClipContentFormat.Html, Hash = "h" };
            await Measure($"BuildDisplayStrings richtext (list entry, no bytes) {size / 1024} KB", () => Task.FromResult(ClipDisplayFormatter.BuildDisplayStrings(rich)), runs: 7);
        }
    }

    private sealed class FakeEmbedder(int dims) : IEmbeddingService
    {
        public int Dimensions => dims;
        public bool IsReady => true;
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            var v = new float[dims];
            var r = new Random(text.GetHashCode());
            for (var i = 0; i < dims; i++) v[i] = (float)r.NextDouble();
            return Task.FromResult(v);
        }
        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            var list = new List<float[]>();
            foreach (var t in texts) list.Add(await EmbedAsync(t, cancellationToken));
            return list;
        }
    }

    [BenchmarkFact]
    public async Task P6_SemanticCache()
    {
        var lib = await PerfLibrary.OpenAsync(Clips, Images, Password);
        Log(Header(lib) + " :: P6 semantic cache (384 dims)");
        const int dims = 384;
        if (await ScalarAsync(lib, "SELECT COUNT(*) FROM clip_embeddings;") < 1000)
        {
            await using var c = lib.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO clip_embeddings (clip_id, model_version, dimensions, vector, created_at) SELECT id, 'm', $d, $v, 'x' FROM clips WHERE id = $id;";
            var pid = cmd.Parameters.Add("$id", SqliteType.Integer);
            cmd.Parameters.AddWithValue("$d", dims);
            var pv = cmd.Parameters.Add("$v", SqliteType.Blob);
            var rng = new Random(1);
            var bytes = new byte[dims * 4];
            for (var id = 1; id <= Clips; id++)
            {
                rng.NextBytes(bytes);
                pid.Value = id;
                pv.Value = bytes;
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        var semantic = new SemanticSearchService(lib.Store, new FakeEmbedder(dims));
        await Measure("RefreshCacheAsync (startup load of all vectors)", () => semantic.RefreshCacheAsync(), runs: 3);
        Log($"    -> cached={semantic.CachedCount}");
        await Measure("QueryAsync topK=400", () => semantic.QueryAsync("invoice meeting", 400));
        var victim = 50_000L;
        await Measure("RemoveEmbeddingsAsync(1 id)  [per evicted/deleted clip]", async () => await semantic.RemoveEmbeddingsAsync([victim--]));
    }

    /// <summary>U-C4b: what secure_delete costs on deletes and on a retention purge.</summary>
    [BenchmarkFact]
    public async Task P15_DeleteAndPurge()
    {
        var lib = await PerfLibrary.OpenAsync(Clips, Images, Password);
        Log(Header(lib) + " :: P15 delete and retention purge");
        lib.SettingsService.SetCurrent(new AppSettings { EnableMaxEntryCount = false, EnableMaxLibrarySize = false });

        async Task<List<long>> Ids(string sql)
        {
            await using var c = lib.ConnectionFactory.CreateConnection();
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            var ids = new List<long>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) ids.Add(r.GetInt64(0));
            return ids;
        }

        async Task TimeDeletes(string label, List<long> ids)
        {
            if (ids.Count == 0)
            {
                Log($"{label,-70} skipped: the library is too small for the fixed offsets");
                return;
            }

            var times = new List<double>();
            foreach (var id in ids)
            {
                var sw = Stopwatch.StartNew();
                await lib.Store.DeleteAsync(id);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Log($"{label,-70} median={times[times.Count / 2],9:F1} ms  min={times[0],9:F1}  max={times[^1],9:F1}  n={times.Count}");
        }

        await TimeDeletes("DeleteAsync text clip", await Ids("SELECT id FROM clips WHERE content_type = 'text' ORDER BY id LIMIT 30 OFFSET 20000;"));
        await TimeDeletes("DeleteAsync image clip (0.4-1.8 MB)", await Ids("SELECT id FROM clips WHERE content_type = 'image' ORDER BY id LIMIT 10 OFFSET 300;"));

        var before = await ScalarAsync(lib, "SELECT COUNT(*) FROM clips;");
        lib.SettingsService.SetCurrent(new AppSettings { EnableMaxEntryCount = false, EnableMaxLibrarySize = false, EnableNormalClipLifetime = true, NormalClipLifetimeDays = 230 });
        var purge = Stopwatch.StartNew();
        await lib.Store.ApplyMaintenanceAsync();
        purge.Stop();
        var purged = before - await ScalarAsync(lib, "SELECT COUNT(*) FROM clips;");
        Log($"{"ApplyMaintenanceAsync retention purge (single run)",-70} median={purge.Elapsed.TotalMilliseconds,9:F1} ms  purged={purged}");
    }

    /// <summary>
    /// U-E1: InitializeAsync on a library seeded before U-E1 (schema 8, inline
    /// payloads), which runs the payload move and every other version-gated step,
    /// the full FTS rebuild among them. Point CLIPTHROUGH_BENCH_DIR at a fresh copy;
    /// on an already-moved library it reports that and times nothing useful.
    /// </summary>
    [BenchmarkFact]
    public async Task P16_PayloadMigration()
    {
        var name = $"lib_{Clips}_{Images}{(string.IsNullOrEmpty(Password) ? "" : "_enc")}.db";
        var lib = new PerfLibrary(Path.Combine(PerfLibrary.Root, name), Password);
        var version = await ScalarAsync(lib, "SELECT CAST(value AS INTEGER) FROM app_metadata WHERE key = 'schema_version';");
        var inline = await ScalarAsync(lib, "SELECT COUNT(*) FROM clips WHERE content_bytes IS NOT NULL OR source_app_icon IS NOT NULL;");
        Log($"=== {DateTime.Now:HH:mm:ss} db={name} enc={!string.IsNullOrEmpty(Password)} :: P16 payload migration (schema {version}, {inline} rows with inline payloads)");

        var sw = Stopwatch.StartNew();
        await lib.Initializer.InitializeAsync();
        sw.Stop();

        var moved = await ScalarAsync(lib, "SELECT COUNT(*) FROM clip_payloads;");
        var left = await ScalarAsync(lib, "SELECT COUNT(*) FROM clips WHERE content_bytes IS NOT NULL OR source_app_icon IS NOT NULL;");
        Log($"{"InitializeAsync from schema 8 (all gated steps + U-E1 move)",-70} median={sw.Elapsed.TotalMilliseconds,9:F1} ms  payloads={moved} leftInline={left}");
    }
}
