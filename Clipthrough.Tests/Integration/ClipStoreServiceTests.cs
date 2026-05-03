using System.Threading.Tasks;
using System.Text;
using Clipthrough.Localization;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Integration;

public sealed class ClipStoreServiceTests
{
    [Fact]
    public async Task CaptureAsync_PersistsRichContentMetadata()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.RichText,
            ContentFormat = ClipContentFormat.Rtf,
            ContentBytes = Encoding.UTF8.GetBytes(@"{\rtf1\ansi Hello\par world}"),
            SourceApp = "Word",
            SourceAppPath = @"C:\Program Files\Word\word.exe",
            IncrementExistingCopyCount = true,
        });

        Assert.NotNull(clip);
        Assert.Equal(ContentType.RichText, clip!.ContentType);
        Assert.Equal(ClipContentFormat.Rtf, clip.ContentFormat);
        Assert.Equal("Word", clip.SourceApp);
        Assert.Equal(1, clip.CopyCount);
        Assert.Contains("Hello", clip.Content);
    }

    [Fact]
    public async Task CaptureAsync_DuplicatePayloadIncrementsCopyCount()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var request = new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "hello world",
            ContentBytes = Encoding.UTF8.GetBytes("hello world"),
            SourceApp = "Editor",
            IncrementExistingCopyCount = true,
        };

        var first = await scope.ClipStoreService.CaptureAsync(request);
        var second = await scope.ClipStoreService.CaptureAsync(request);
        var results = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = "Editor" });

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Single(results.Items);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(2, results.Items[0].CopyCount);
    }

    [Fact]
    public async Task CaptureAsync_RejectsOversizedPayloads()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 256 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentBytes = new byte[257],
            ContentText = new string('a', 257),
        });

        var results = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters());

        Assert.Null(clip);
        Assert.Empty(results.Items);
        Assert.Equal(AppText.ClipCaptureFailedTitle, scope.NotificationService.LastNotification?.Title);
        Assert.Equal(AppNotificationLevel.Warning, scope.NotificationService.LastNotification?.Level);
    }

    [Fact]
    public async Task CaptureAsync_PersistsWindowTitleAndSourceUrl()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "test content",
            ContentBytes = Encoding.UTF8.GetBytes("test content"),
            SourceApp = "Chrome",
            SourceWindowTitle = "Google - Chrome",
            SourceUrl = "https://www.google.com",
        });

        Assert.NotNull(clip);
        Assert.Equal("Google - Chrome", clip!.SourceWindowTitle);
        Assert.Equal("https://www.google.com", clip.SourceUrl);

        var results = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters());
        Assert.Single(results.Items);
        Assert.Equal("Google - Chrome", results.Items[0].SourceWindowTitle);
        Assert.Equal("https://www.google.com", results.Items[0].SourceUrl);
    }

    [Fact]
    public async Task CaptureAsync_DuplicatePreservesWindowTitleAndUrl()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var request = new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "dup content",
            ContentBytes = Encoding.UTF8.GetBytes("dup content"),
            SourceApp = "Chrome",
            SourceWindowTitle = "Page Title",
            SourceUrl = "https://example.com",
            IncrementExistingCopyCount = true,
        };

        await scope.ClipStoreService.CaptureAsync(request);
        var second = await scope.ClipStoreService.CaptureAsync(request);

        Assert.NotNull(second);
        Assert.Equal("Page Title", second!.SourceWindowTitle);
        Assert.Equal("https://example.com", second.SourceUrl);
    }

    [Fact]
    public async Task CaptureAsync_SkipPostInsertMaintenance_DefersPurgeUntilManualMaintenance()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings
        {
            MaxClipSizeBytes = 4096,
            EnableMaxEntryCount = true,
            MaxEntryCount = 1,
        });

        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "first",
            ContentBytes = Encoding.UTF8.GetBytes("first"),
            SkipPostInsertMaintenance = true,
        });

        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "second",
            ContentBytes = Encoding.UTF8.GetBytes("second"),
            SkipPostInsertMaintenance = true,
        });

        var beforeMaintenance = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters());
        Assert.Equal(2, beforeMaintenance.TotalClipCount);

        await scope.ClipStoreService.ApplyMaintenanceAsync();

        var afterMaintenance = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters());
        Assert.Equal(1, afterMaintenance.TotalClipCount);
        Assert.Single(afterMaintenance.Items);
        Assert.Equal("second", afterMaintenance.Items[0].Content);
    }

    [Fact]
    public async Task CaptureFastAsync_DefersSensitivityUntilApplied()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "password=supersecret",
            ContentBytes = Encoding.UTF8.GetBytes("password=supersecret"),
        });

        Assert.NotNull(clip);
        Assert.False(clip!.IsSensitive);
        Assert.Empty(clip.SensitivityMatches);

        var updated = await scope.ClipStoreService.ApplySensitivityAsync(clip.Id);

        Assert.NotNull(updated);
        Assert.True(updated!.IsSensitive);
        Assert.NotEmpty(updated.SensitivityMatches);
    }

    [Fact]
    public async Task CaptureFastAsync_DuplicatePayloadIncrementsCopyCount()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var request = new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "fast duplicate",
            ContentBytes = Encoding.UTF8.GetBytes("fast duplicate"),
            IncrementExistingCopyCount = true,
        };

        var first = await scope.ClipStoreService.CaptureFastAsync(request);
        var second = await scope.ClipStoreService.CaptureFastAsync(request);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(2, second.CopyCount);
    }

    [Fact]
    public async Task UpdateDeferredContentAsync_UpgradesPlainTextCaptureToRichContent()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "Hello rich",
            ContentBytes = Encoding.UTF8.GetBytes("Hello rich"),
        });

        Assert.NotNull(clip);

        var updated = await scope.ClipStoreService.UpdateDeferredContentAsync(clip!.Id, new ClipCaptureRequest
        {
            ContentType = ContentType.RichText,
            ContentFormat = ClipContentFormat.Html,
            ContentText = "Hello rich",
            ContentBytes = Encoding.UTF8.GetBytes("<p><strong>Hello rich</strong></p>"),
        });

        Assert.NotNull(updated);
        Assert.Equal(ContentType.RichText, updated!.ContentType);
        Assert.Equal(ClipContentFormat.Html, updated.ContentFormat);
        Assert.Equal("Hello rich", updated.Content);
        Assert.Contains("<strong>", Encoding.UTF8.GetString(updated.ContentBytes!));
    }

    [Fact]
    public async Task MarkPastedAsync_TracksPasteState()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "paste me",
            ContentBytes = Encoding.UTF8.GetBytes("paste me"),
        });

        Assert.NotNull(clip);
        Assert.False(clip!.IsPasted);
        Assert.Equal(0, clip.PasteCount);

        await scope.ClipStoreService.MarkPastedAsync(clip.Id);

        var results = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters());
        var updated = results.Items[0];
        Assert.True(updated.IsPasted);
        Assert.Equal(1, updated.PasteCount);
        Assert.NotNull(updated.LastPastedAt);
    }

    [Fact]
    public async Task MarkPastedAsync_IncrementsPasteCount()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "multi paste",
            ContentBytes = Encoding.UTF8.GetBytes("multi paste"),
        });

        Assert.NotNull(clip);
        await scope.ClipStoreService.MarkPastedAsync(clip!.Id);
        await scope.ClipStoreService.MarkPastedAsync(clip.Id);
        await scope.ClipStoreService.MarkPastedAsync(clip.Id);

        var results = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters());
        Assert.Equal(3, results.Items[0].PasteCount);
    }

    [Fact]
    public async Task SearchAsync_PastedOnlyFilter()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip1 = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "pasted clip",
            ContentBytes = Encoding.UTF8.GetBytes("pasted clip"),
        });
        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "unpasted clip",
            ContentBytes = Encoding.UTF8.GetBytes("unpasted clip"),
        });

        Assert.NotNull(clip1);
        await scope.ClipStoreService.MarkPastedAsync(clip1!.Id);

        var pastedResults = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { PastedOnly = true });
        Assert.Single(pastedResults.Items);
        Assert.Equal("pasted clip", pastedResults.Items[0].Content);

        var allResults = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters());
        Assert.Equal(2, allResults.Items.Count);
    }

    [Fact]
    public async Task SearchAsync_FindsByWindowTitleViaFts()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "some text",
            ContentBytes = Encoding.UTF8.GetBytes("some text"),
            SourceWindowTitle = "UniqueWindowTitle",
        });

        var results = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = "UniqueWindowTitle" });
        Assert.Single(results.Items);
    }

    [Fact]
    public async Task SearchAsync_WildcardMatchesPartialContent()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "hello world",
            ContentBytes = Encoding.UTF8.GetBytes("hello world"),
        });
        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "goodbye world",
            ContentBytes = Encoding.UTF8.GetBytes("goodbye world"),
        });

        var results = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters
        {
            SearchText = "hel*",
            UseWildcard = true,
        });
        Assert.Single(results.Items);
        Assert.Equal("hello world", results.Items[0].Content);
    }

    [Fact]
    public async Task SearchAsync_WholeWordExcludesPartialMatches()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "cat is here",
            ContentBytes = Encoding.UTF8.GetBytes("cat is here"),
        });
        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "category of items",
            ContentBytes = Encoding.UTF8.GetBytes("category of items"),
        });

        var results = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters
        {
            SearchText = "cat",
            WholeWord = true,
        });
        Assert.Single(results.Items);
        Assert.Equal("cat is here", results.Items[0].Content);
    }

    [Fact]
    public async Task SearchHistory_SavesAndRetrievesQueries()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        await scope.SearchHistoryService.SaveSearchAsync("first query");
        await scope.SearchHistoryService.SaveSearchAsync("second query");

        var results = await scope.SearchHistoryService.GetRecentSearchesAsync();
        Assert.Equal(2, results.Count);
        Assert.Equal("second query", results[0]);
        Assert.Equal("first query", results[1]);
    }

    [Fact]
    public async Task SearchHistory_DeduplicatesOnSave()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        await scope.SearchHistoryService.SaveSearchAsync("test");
        await scope.SearchHistoryService.SaveSearchAsync("other");
        await scope.SearchHistoryService.SaveSearchAsync("test");

        var results = await scope.SearchHistoryService.GetRecentSearchesAsync();
        Assert.Equal(2, results.Count);
        Assert.Equal("test", results[0]);
    }

    [Fact]
    public async Task SearchHistory_ClearRemovesAll()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        await scope.SearchHistoryService.SaveSearchAsync("query1");
        await scope.SearchHistoryService.SaveSearchAsync("query2");
        await scope.SearchHistoryService.ClearAsync();

        var results = await scope.SearchHistoryService.GetRecentSearchesAsync();
        Assert.Empty(results);
    }

    [Fact]
    public async Task Embeddings_ClaimSaveAndCoverage_RoundTrip()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        for (var i = 0; i < 3; i++)
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"hello {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"hello {i}"),
                SourceApp = "Editor",
                IncrementExistingCopyCount = true,
            });
        }

        var initialCoverage = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(3, initialCoverage.EligibleTotal);
        Assert.Equal(0, initialCoverage.Embedded);
        Assert.Equal(3, initialCoverage.Pending);

        var claimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Equal(3, claimed.Count);
        Assert.All(claimed, c => Assert.False(string.IsNullOrWhiteSpace(c.TextToEmbed)));

        // After claim: none are pending (all processing), none yet succeeded.
        var claimedCoverage = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(0, claimedCoverage.Embedded);
        Assert.Equal(3, claimedCoverage.Pending); // processing counts as pending in coverage

        var records = new System.Collections.Generic.List<ClipEmbeddingRecord>();
        foreach (var c in claimed)
        {
            var vec = new float[8];
            for (var k = 0; k < vec.Length; k++) vec[k] = 0.35355339f; // L2 norm = 1 for 8 dims
            records.Add(new ClipEmbeddingRecord(c.ClipId, vec));
        }

        await scope.ClipStoreService.SaveEmbeddingBatchAsync(records, "test-model-v1");

        var finalCoverage = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(3, finalCoverage.Embedded);
        Assert.Equal(0, finalCoverage.Pending);

        var loaded = await scope.ClipStoreService.LoadAllEmbeddingsAsync();
        Assert.Equal(3, loaded.Count);
        Assert.All(loaded, e => Assert.Equal(8, e.Vector.Length));
        Assert.All(loaded, e => Assert.Equal("test-model-v1", e.ModelVersion));

        // Second claim returns nothing (all succeeded).
        var reclaim = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Empty(reclaim);
    }

    [Fact]
    public async Task Embeddings_SaveBatch_SkipsDeletedClaimedClip()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "embed then delete",
            ContentBytes = Encoding.UTF8.GetBytes("embed then delete"),
            SourceApp = "Editor",
        });

        Assert.NotNull(clip);

        var claimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(1);
        Assert.Single(claimed);

        await scope.ClipStoreService.DeleteAsync(clip!.Id);

        var exception = await Record.ExceptionAsync(() => scope.ClipStoreService.SaveEmbeddingBatchAsync(
            [new ClipEmbeddingRecord(clip.Id, [1f, 0f, 0f, 0f])],
            "test-model-v1"));

        Assert.Null(exception);
        Assert.Null(await scope.ClipStoreService.GetByIdAsync(clip.Id));
        Assert.Empty(await scope.ClipStoreService.LoadAllEmbeddingsAsync());
    }

    [Fact]
    public async Task Embeddings_SensitiveClipExcluded_AndPurgedOnFlag()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "secret stuff",
            ContentBytes = Encoding.UTF8.GetBytes("secret stuff"),
            SourceApp = "Editor",
            IncrementExistingCopyCount = true,
        });
        Assert.NotNull(clip);

        var claimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Single(claimed);
        var vec = new float[4] { 0.5f, 0.5f, 0.5f, 0.5f };
        await scope.ClipStoreService.SaveEmbeddingBatchAsync(
            new[] { new ClipEmbeddingRecord(clip!.Id, vec) },
            "test-model-v1");

        Assert.Single(await scope.ClipStoreService.LoadAllEmbeddingsAsync());

        // Mark sensitive: embedding should be purged, clip flagged excluded.
        await scope.ClipStoreService.SetSensitiveAsync(clip.Id, true);

        Assert.Empty(await scope.ClipStoreService.LoadAllEmbeddingsAsync());
        var coverage = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(0, coverage.EligibleTotal);
        Assert.Equal(1, coverage.Excluded);
    }

    [Fact]
    public async Task Embeddings_MarkAllForRerun_PurgesAndRequeues()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var c1 = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "alpha",
            ContentBytes = Encoding.UTF8.GetBytes("alpha"),
            SourceApp = "Editor",
            IncrementExistingCopyCount = true,
        });

        var claimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        await scope.ClipStoreService.SaveEmbeddingBatchAsync(
            new[] { new ClipEmbeddingRecord(c1!.Id, new float[] { 1f, 0f, 0f, 0f }) },
            "v1");

        Assert.Single(await scope.ClipStoreService.LoadAllEmbeddingsAsync());

        var rerunIds = await scope.ClipStoreService.MarkAllEmbeddingsForRerunAsync();
        Assert.Contains(c1.Id, rerunIds);
        Assert.Empty(await scope.ClipStoreService.LoadAllEmbeddingsAsync());

        var claimedAgain = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Single(claimedAgain);
        Assert.Equal(c1.Id, claimedAgain[0].ClipId);
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent_AndSurvivesLegacySchema()
    {
        // Regression: prior build shipped a bug where Schema DDL referenced the new
        // embedding_status column in a CREATE INDEX before the migration added it,
        // causing startup failure on any pre-existing database.
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });
        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "persist me",
            ContentBytes = Encoding.UTF8.GetBytes("persist me"),
            SourceApp = "Editor",
            IncrementExistingCopyCount = true,
        });

        // Drop the column + table + index to simulate a pre-embedding DB, then re-initialize.
        await using (var conn = scope.ConnectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DROP INDEX IF EXISTS idx_clips_embedding_status;
                DROP TABLE IF EXISTS clip_embeddings;
                ALTER TABLE clips DROP COLUMN embedding_status;
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Re-running InitializeAsync must add the column + table + index without error.
        await scope.DatabaseInitializer.InitializeAsync();

        var coverage = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(1, coverage.EligibleTotal);
        Assert.Equal(0, coverage.Embedded);
    }
}
