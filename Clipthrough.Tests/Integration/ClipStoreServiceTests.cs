using System;
using System.Threading.Tasks;
using System.Text;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Integration;

public sealed class ClipStoreServiceTests
{
    /// <summary>
    /// Synthetic, obviously-fake credential text. It is not a real secret; it
    /// exists only because it matches the built-in "Passwords" rule
    /// ((?i)(password|passwd|pwd)['":\s=]+\S{6,}), which is what these tests
    /// need in order to observe a classification actually happening.
    /// </summary>
    private const string SyntheticSecretText = "password = NOT-A-REAL-CREDENTIAL";

    /// <summary>
    /// Regression test for C2: CaptureFastAsync writes content to disk and to
    /// the FTS index before classifying it, relying on a follow-up
    /// ApplySensitivityAsync from the clipboard monitor. If that follow-up never
    /// ran (crash, SQLITE_BUSY, faulted enrichment task) nothing distinguished
    /// the clip from one that had been scanned and found clean, so a secret
    /// stayed unflagged forever. Startup recovery must find and classify it.
    /// </summary>
    [Fact]
    public async Task ApplyPendingSensitivityAsync_ClassifiesClipsWhoseDeferredScanNeverRan()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        // Guard: the fixture is only meaningful if a rule really matches it.
        await scope.SensitivityService.ReloadAsync();
        Assert.NotEmpty(scope.SensitivityService.Scan(SyntheticSecretText));

        // Fast capture defers classification - this simulates the app dying
        // before EnrichCapturedClipAsync got to run.
        var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = SyntheticSecretText,
            ContentBytes = Encoding.UTF8.GetBytes(SyntheticSecretText),
        });

        Assert.NotNull(clip);
        Assert.False(clip!.IsSensitive);

        var classified = await scope.ClipStoreService.ApplyPendingSensitivityAsync();
        Assert.Equal(1, classified);

        var recovered = await scope.ClipStoreService.GetByIdAsync(clip.Id);
        Assert.NotNull(recovered);
        Assert.True(recovered!.IsSensitive, "A clip whose deferred scan never ran must be classified on recovery.");
    }

    /// <summary>
    /// Recovery must be idempotent: a clip that has already been classified is
    /// not rescanned, so startup cost stays proportional to the actual backlog.
    /// </summary>
    [Fact]
    public async Task ApplyPendingSensitivityAsync_SkipsAlreadyClassifiedClips()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = SyntheticSecretText,
            ContentBytes = Encoding.UTF8.GetBytes(SyntheticSecretText),
        });
        Assert.NotNull(clip);

        Assert.Equal(1, await scope.ClipStoreService.ApplyPendingSensitivityAsync());
        Assert.Equal(0, await scope.ClipStoreService.ApplyPendingSensitivityAsync());
    }

    /// <summary>
    /// A clip captured through the full CaptureAsync path is classified inline,
    /// so recovery has nothing to do for it.
    /// </summary>
    [Fact]
    public async Task ApplyPendingSensitivityAsync_IgnoresClipsScannedAtCapture()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = SyntheticSecretText,
            ContentBytes = Encoding.UTF8.GetBytes(SyntheticSecretText),
        });

        Assert.NotNull(clip);
        Assert.True(clip!.IsSensitive);
        Assert.Equal(0, await scope.ClipStoreService.ApplyPendingSensitivityAsync());
    }

    /// <summary>
    /// Regression test for C2: an unclassified clip has is_sensitive = 0 just
    /// like a clean one, so the embedding worker used to accept it as a
    /// candidate. Unclassified secrets could therefore reach the vector cache
    /// before anything decided they were secrets.
    /// </summary>
    [Fact]
    public async Task ClaimPendingEmbeddingsAsync_SkipsClipsAwaitingClassification()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = SyntheticSecretText,
            ContentBytes = Encoding.UTF8.GetBytes(SyntheticSecretText),
        });

        var beforeClassification = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Empty(beforeClassification);

        // After classification the clip is still excluded, now for the right
        // reason: it is sensitive.
        await scope.ClipStoreService.ApplyPendingSensitivityAsync();
        var afterClassification = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Empty(afterClassification);
    }

    /// <summary>
    /// The classification gate must not permanently block ordinary clips from
    /// being embedded - once the deferred scan completes and finds nothing, the
    /// clip becomes a normal embedding candidate.
    /// </summary>
    [Fact]
    public async Task ClaimPendingEmbeddingsAsync_AcceptsCleanClipsOnceClassified()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "an entirely ordinary sentence",
            ContentBytes = Encoding.UTF8.GetBytes("an entirely ordinary sentence"),
        });
        Assert.NotNull(clip);

        Assert.Empty(await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10));

        await scope.ClipStoreService.ApplyPendingSensitivityAsync();

        var claimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Single(claimed);
        Assert.Equal(clip!.Id, claimed[0].ClipId);
    }

    /// <summary>
    /// CaptureBatchAsync classifies inline, so its clips must be stamped as
    /// scanned. Leaving the marker unset excluded every bulk-imported clip from
    /// embedding until the next launch, and then made the startup pass rescan
    /// the whole import one clip at a time.
    /// </summary>
    [Fact]
    public async Task CaptureBatchAsync_MarksImportedClipsAsClassified()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var request = new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "an imported sentence",
            ContentBytes = Encoding.UTF8.GetBytes("an imported sentence"),
            IncrementExistingCopyCount = true,
        };

        var result = await scope.ClipStoreService.CaptureBatchAsync([request]);
        Assert.Equal(1, result.Imported);

        // Eligible immediately: no restart and no deferred rescan required.
        Assert.Single(await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10));

        // Re-importing the same content must not clear the marker either.
        await scope.ClipStoreService.CaptureBatchAsync([request]);
        Assert.Equal(0, await CountUnclassifiedAsync(scope));
    }

    private static async Task<long> CountUnclassifiedAsync(TemporaryDatabaseScope scope)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM clips WHERE sensitivity_scanned_at IS NULL;";
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

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
    public async Task CaptureFastAsync_PersistsImportKind()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "imported via drag",
            ContentBytes = Encoding.UTF8.GetBytes("imported via drag"),
            ImportKind = ClipImportKinds.DragDrop,
        });

        Assert.NotNull(clip);
        Assert.Equal(ClipImportKinds.DragDrop, clip!.ImportKind);

        var result = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { Limit = 10 });
        var loaded = Assert.Single(result.Items);
        Assert.Equal(ClipImportKinds.DragDrop, loaded.ImportKind);
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
    public async Task SearchAsync_FindsSubstringWithinToken()
    {
        // Regression: with the unicode61 tokenizer, "poc1" failed to match
        // "INGEST-DIRECTBONDPOC1" because FTS5 prefix queries can't match a
        // substring inside an indexed token. The trigram tokenizer fixes this.
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "INGEST-DIRECTBONDPOC1",
            ContentBytes = Encoding.UTF8.GetBytes("INGEST-DIRECTBONDPOC1"),
        });

        var fullMatch = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = "INGEST-DIRECTBONDPOC1" });
        Assert.Single(fullMatch.Items);

        var substringMatch = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = "poc1" });
        Assert.Single(substringMatch.Items);

        var caseInsensitiveMatch = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = "POC1" });
        Assert.Single(caseInsensitiveMatch.Items);
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

    // Bug #7: a clip whose inference always fails must stop being re-claimed after
    // MaxEmbeddingAttempts (3) failures instead of being re-embedded every idle
    // cycle forever. RerunAll resets the counter for a fresh set of attempts.
    [Fact]
    public async Task Embeddings_PoisonClip_StopsBeingReclaimedAfterMaxAttempts()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "poison",
            ContentBytes = Encoding.UTF8.GetBytes("poison"),
            SourceApp = "Editor",
            IncrementExistingCopyCount = true,
        });
        Assert.NotNull(clip);

        // Three claim+fail cycles (the retry cap). Each must still hand the clip out.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var claimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
            Assert.Single(claimed);
            Assert.Equal(clip!.Id, claimed[0].ClipId);
            await scope.ClipStoreService.SetEmbeddingFailureAsync(clip.Id, "boom");
        }

        // Fourth claim: the cap is reached, so the poison clip is no longer offered.
        Assert.Empty(await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10));

        // RerunAll resets the attempt counter — the clip becomes claimable again.
        await scope.ClipStoreService.MarkAllEmbeddingsForRerunAsync();
        var afterRerun = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Single(afterRerun);
        Assert.Equal(clip!.Id, afterRerun[0].ClipId);
    }

    // Bug #8: GetByIdsAsync feeds the list (semantic-search additions), so it must
    // use the metadata-only read model and omit image bytes — the per-clip hydrator
    // (GetByIdAsync) loads them on select. Loading every BLOB here is wasteful.
    [Fact]
    public async Task GetByIdsAsync_OmitsImageBytes_ButFullReadCarriesThem()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1 << 20 });

        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentText = string.Empty,
            ContentBytes = png,
            SourceApp = "Editor",
            IncrementExistingCopyCount = true,
        });
        Assert.NotNull(clip);

        var byIds = await scope.ClipStoreService.GetByIdsAsync(new[] { clip!.Id });
        var fetched = Assert.Single(byIds);
        Assert.Equal(clip.Id, fetched.Id);
        Assert.Equal(ContentType.Image, fetched.ContentType);
        Assert.Null(fetched.ContentBytes);

        // The full-content read (the hydrator) still carries the bytes.
        var full = await scope.ClipStoreService.GetByIdAsync(clip.Id);
        Assert.NotNull(full!.ContentBytes);
        Assert.Equal(png, full.ContentBytes);
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

        // Drop the column + table + index to simulate a pre-embedding DB,
        // then clear the schema_version row so the version-gate in
        // DatabaseInitializer treats this as an upgrade (not a no-op) and
        // re-runs every Ensure helper.
        await using (var conn = scope.ConnectionFactory.CreateConnection())
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DROP INDEX IF EXISTS idx_clips_embedding_status;
                DROP TABLE IF EXISTS clip_embeddings;
                ALTER TABLE clips DROP COLUMN embedding_status;
                DELETE FROM app_metadata WHERE key = 'schema_version';
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Re-running InitializeAsync must add the column + table + index without error.
        await scope.DatabaseInitializer.InitializeAsync();

        var coverage = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(1, coverage.EligibleTotal);
        Assert.Equal(0, coverage.Embedded);
    }

    // ======== U12: Split read model — no BLOBs in list/search queries ========

    [Fact]
    public async Task SearchAsync_ListQuery_DoesNotMaterializeContentBytesOrIconBytes()
    {
        // Arrange: seed an image clip with non-trivial content_bytes and source_app_icon.
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });

        var imageBytes = new byte[1024];
        new Random(42).NextBytes(imageBytes);
        var iconBytes = new byte[256];
        new Random(43).NextBytes(iconBytes);

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentBytes = imageBytes,
            SourceAppIconBytes = iconBytes,
            ImageWidth = 16,
            ImageHeight = 16,
        });
        Assert.NotNull(clip);

        // Act: SearchAsync (the list/FTS path) must return the clip without loading BLOBs.
        var result = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { Limit = 50 });

        Assert.Single(result.Items);
        var listed = result.Items[0];
        Assert.Equal(clip!.Id, listed.Id);
        Assert.Null(listed.ContentBytes);           // BLOB omitted (U12)
        Assert.Null(listed.SourceAppIconBytes);     // BLOB omitted (U12)
        Assert.True(listed.SourceAppIconAvailable); // but flag is set (U12)
        Assert.Equal(ContentType.Image, listed.ContentType);
        Assert.Equal(16, listed.ImageWidth);
        Assert.Equal(16, listed.ImageHeight);
    }

    [Fact]
    public async Task GetByIdAsync_StillReturnsFullBytes()
    {
        // Full bytes must be available via GetByIdAsync (used on select/open).
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });

        var imageBytes = new byte[128];
        new Random(7).NextBytes(imageBytes);

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentBytes = imageBytes,
        });
        Assert.NotNull(clip);

        var full = await scope.ClipStoreService.GetByIdAsync(clip!.Id);
        Assert.NotNull(full);
        Assert.NotNull(full!.ContentBytes);
        Assert.Equal(imageBytes.Length, full.ContentBytes!.Length);
    }

    [Fact]
    public async Task SearchAsync_InMemoryPath_DoesNotMaterializeContentBytes()
    {
        // Regex / case-sensitive / wildcard paths also use ClipListSelectColumns.
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });

        var imageBytes = new byte[512];
        new Random(11).NextBytes(imageBytes);

        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentBytes = imageBytes,
        });

        // Force in-memory search path (regex flag).
        var result = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters
        {
            SearchText = ".*",
            UseRegex = true,
            Limit = 50,
        });

        Assert.Single(result.Items);
        Assert.Null(result.Items[0].ContentBytes);    // BLOB omitted (U12)
    }

    // ======== U13: Regex hoisted + field parity ========

    [Fact]
    public async Task SearchAsync_RegexPath_MatchesOcrText()
    {
        // OcrText must be searched in the regex path (was missing before U13).
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentBytes = new byte[16],
        });
        Assert.NotNull(clip);
        await scope.ClipStoreService.SetOcrResultAsync(clip!.Id, "UNIQUE_OCR_TOKEN_XYZ");

        // Should match via OcrText.
        var byRegex = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters
        {
            SearchText = "UNIQUE_OCR_TOKEN_XYZ",
            UseRegex = true,
            Limit = 50,
        });
        Assert.NotEmpty(byRegex.Items);
        Assert.Contains(byRegex.Items, c => c.Id == clip.Id);
    }

    [Fact]
    public async Task SearchAsync_RegexPath_MatchesSourceWindowTitle()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "hello",
            ContentBytes = Encoding.UTF8.GetBytes("hello"),
            SourceWindowTitle = "UniqueWindowTitle_ABC",
        });

        var result = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters
        {
            SearchText = "UniqueWindowTitle_ABC",
            UseRegex = true,
            Limit = 50,
        });
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task SearchAsync_PlainTextPath_MatchesOcrText()
    {
        // Plain-text (short token) in-memory path must also match OcrText.
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentBytes = new byte[16],
        });
        Assert.NotNull(clip);
        await scope.ClipStoreService.SetOcrResultAsync(clip!.Id, "INMEMORYTESTTOKEN");

        // "IN" is 2 chars — below FTS 3-char minimum, forces in-memory path.
        var result = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters
        {
            SearchText = "IN",
            CaseSensitive = true,
            Limit = 50,
        });
        Assert.NotEmpty(result.Items);
        Assert.Contains(result.Items, c => c.Id == clip.Id);
    }

    // ======== U15: approximate count (no separate full COUNT per keystroke) ========

    [Fact]
    public async Task SearchAsync_FullPage_ReportsAtLeastLimitPlusOne()
    {
        // When more results exist than Limit, TotalMatchingCount > Limit (no exact COUNT scan).
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        const int Limit = 5;
        for (var i = 0; i < Limit + 3; i++)
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"item {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"item {i}"),
                IncrementExistingCopyCount = true,
            });
        }

        var result = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { Limit = Limit, Offset = 0 });

        Assert.Equal(Limit, result.Items.Count);
        Assert.True(result.TotalMatchingCount > Limit,
            $"Expected TotalMatchingCount > {Limit} but was {result.TotalMatchingCount}");
    }
}
