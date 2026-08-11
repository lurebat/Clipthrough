using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Regression coverage for the <c>clips_au</c> FTS5 trigger scope.
///
/// The trigger was originally declared <c>AFTER UPDATE ON clips</c>, with no
/// column list, so every metadata-only write — paste counters, OCR status,
/// embedding status, pinning — deleted and re-tokenised the clip's entire
/// content in the trigram index. Clip content can be megabytes; the metadata
/// being written is a handful of bytes. Two independent reviews measured the
/// resulting write amplification at 38-93x.
///
/// These tests pin both halves of the fix: metadata writes must not touch the
/// index, and content writes must still keep it correct.
/// </summary>
public sealed class ClipSearchIndexTriggerTests
{
    [Fact]
    public async Task MetadataOnlyUpdates_DoNotRewriteSearchIndex()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4_000_000 });

        var clip = await CaptureAsync(scope, "indexable haystack content " + new string('x', 200_000));
        var before = CountIndexSegmentRows(scope);

        // The exact writes the app performs constantly: the paste path, the OCR
        // worker, the embedding worker, and pinning. None of them touch an
        // indexed column.
        Execute(scope, $"UPDATE clips SET paste_count = paste_count + 1, is_pasted = 1, last_pasted_at = '2026-01-01T00:00:00Z' WHERE id = {clip.Id};");
        Execute(scope, $"UPDATE clips SET ocr_status = 'completed', ocr_attempted_at = '2026-01-01T00:00:00Z' WHERE id = {clip.Id};");
        Execute(scope, $"UPDATE clips SET embedding_status = 'completed', embedding_attempts = 1 WHERE id = {clip.Id};");
        Execute(scope, $"UPDATE clips SET pinned_at = '2026-01-01T00:00:00Z' WHERE id = {clip.Id};");

        Assert.Equal(before, CountIndexSegmentRows(scope));
    }

    [Fact]
    public async Task ContentUpdate_StillReindexesSearchIndex()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await CaptureAsync(scope, "the original payload");
        Assert.Equal(1, CountIndexMatches(scope, "original"));

        Execute(scope, $"UPDATE clips SET content = 'the replacement payload' WHERE id = {clip.Id};");

        Assert.Equal(0, CountIndexMatches(scope, "original"));
        Assert.Equal(1, CountIndexMatches(scope, "replacement"));
    }

    [Fact]
    public async Task OcrTextUpdate_StillReindexesSearchIndex()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await CaptureAsync(scope, "screenshot placeholder");
        Assert.Equal(0, CountIndexMatches(scope, "recognised"));

        Execute(scope, $"UPDATE clips SET ocr_text = 'recognised words' WHERE id = {clip.Id};");

        Assert.Equal(1, CountIndexMatches(scope, "recognised"));
    }

    /// <summary>
    /// Databases created before the trigger was scoped already contain the
    /// unscoped <c>clips_au</c>. <c>CREATE TRIGGER IF NOT EXISTS</c> would
    /// silently leave it in place, so the schema DDL drops it first — meaning
    /// existing users get the fix, not just new installs.
    /// </summary>
    [Fact]
    public async Task Initialize_ReplacesLegacyUnscopedUpdateTrigger()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        Execute(scope, "DROP TRIGGER IF EXISTS clips_au;");
        Execute(scope, """
            CREATE TRIGGER clips_au AFTER UPDATE ON clips BEGIN
                INSERT INTO clips_fts(clips_fts, rowid, content, source_app, source_window_title, source_url, ocr_text)
                VALUES ('delete', old.id, old.content, old.source_app, old.source_window_title, old.source_url, old.ocr_text);
                INSERT INTO clips_fts(rowid, content, source_app, source_window_title, source_url, ocr_text)
                VALUES (new.id, new.content, new.source_app, new.source_window_title, new.source_url, new.ocr_text);
            END;
            """);
        Assert.DoesNotContain("UPDATE OF", ReadTriggerSql(scope));

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Contains("UPDATE OF", ReadTriggerSql(scope));
    }

    private static async Task<ClipEntry> CaptureAsync(TemporaryDatabaseScope scope, string text)
    {
        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = text,
            ContentBytes = Encoding.UTF8.GetBytes(text),
            SourceApp = "Editor",
        });

        Assert.NotNull(clip);
        return clip!;
    }

    /// <summary>
    /// Row count of the FTS5 segment store. Re-indexing a clip appends new
    /// segment rows, so an unchanged count is direct evidence the trigger did
    /// not fire.
    /// </summary>
    private static long CountIndexSegmentRows(TemporaryDatabaseScope scope)
        => Scalar<long>(scope, "SELECT COUNT(*) FROM clips_fts_data;");

    private static long CountIndexMatches(TemporaryDatabaseScope scope, string term)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM clips_fts WHERE clips_fts MATCH $term;";
        command.Parameters.AddWithValue("$term", term);
        return (long)command.ExecuteScalar()!;
    }

    private static string ReadTriggerSql(TemporaryDatabaseScope scope)
        => Scalar<string>(scope, "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'clips_au';");

    private static void Execute(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(TemporaryDatabaseScope scope, string sql)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }
}
