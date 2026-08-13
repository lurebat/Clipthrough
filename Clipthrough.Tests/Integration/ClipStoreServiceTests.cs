using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// An image clip stores only an "Image (WxH)" summary in `content`; anything
    /// sensitive in a screenshot lives in `ocr_text`. SetOcrResultAsync scans it
    /// and flags the clip, but ApplySensitivityAsync used to re-scan `content`
    /// alone — so any later re-scan wiped the matches, cleared is_sensitive, and
    /// flipped embedding_status back off 'excluded', silently declassifying a
    /// screenshot of a credential and re-admitting it to the embedding queue.
    /// </summary>
    [Fact]
    public async Task ApplySensitivityAsync_KeepsSensitivityDetectedInOcrText()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentText = string.Empty,
            ContentBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 },
            ImageWidth = 640,
            ImageHeight = 480,
        });

        Assert.NotNull(clip);
        Assert.True(await scope.ClipStoreService.SetOcrResultAsync(clip!.Id, SyntheticSecretText));

        var afterOcr = Assert.Single(await scope.ClipStoreService.GetByIdsAsync(new[] { clip.Id }));
        Assert.True(afterOcr.IsSensitive, "OCR text carrying a credential must classify the clip as sensitive");

        var rescanned = await scope.ClipStoreService.ApplySensitivityAsync(clip.Id);

        Assert.NotNull(rescanned);
        Assert.True(rescanned!.IsSensitive, "A re-scan must not declassify a clip flagged from its OCR text");
        Assert.NotEmpty(rescanned.SensitivityMatches);
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

    /// <summary>
    /// A multi-token whole-word search has to require every token, the same as
    /// the plain-text path (tokens.All) and the FTS path (AND). This used to
    /// join the tokens into one alternation, so ticking "whole word" silently
    /// turned an AND search into an OR and widened the result set.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WholeWordRequiresEveryToken()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        foreach (var content in new[] { "cat and dog", "cat alone", "dog alone" })
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = content,
                ContentBytes = Encoding.UTF8.GetBytes(content),
            });
        }

        var results = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters
        {
            SearchText = "cat dog",
            WholeWord = true,
        });

        Assert.Single(results.Items);
        Assert.Equal("cat and dog", results.Items[0].Content);
        Assert.Equal(1, results.TotalMatchingCount);
    }

    /// <summary>
    /// Whole word still has to mean whole word when several tokens are given —
    /// requiring every token must not be achieved by falling back to substring
    /// matching.
    /// </summary>
    [Fact]
    public async Task SearchAsync_WholeWordMultiTokenStillExcludesPartialMatches()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        foreach (var content in new[] { "category dogma", "cat dog" })
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = content,
                ContentBytes = Encoding.UTF8.GetBytes(content),
            });
        }

        var results = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters
        {
            SearchText = "cat dog",
            WholeWord = true,
        });

        Assert.Single(results.Items);
        Assert.Equal("cat dog", results.Items[0].Content);
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
    public async Task ResetStalledEmbeddingClaims_ReturnsProcessingRowsToPendingAndLeavesOtherStatesAlone()
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
                ContentText = $"stalled {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"stalled {i}"),
                SourceApp = "Editor",
            });
        }

        var claimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Equal(3, claimed.Count);

        // One of them completes normally; the other two are the interrupted batch.
        var vec = new float[8];
        for (var k = 0; k < vec.Length; k++) vec[k] = 0.35355339f;
        await scope.ClipStoreService.SaveEmbeddingBatchAsync([new ClipEmbeddingRecord(claimed[0].ClipId, vec)], "test-model-v1");

        // Nothing is claimable while the two are stuck in 'processing'.
        Assert.Empty(await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10));

        Assert.Equal(2, await scope.ClipStoreService.ResetStalledEmbeddingClaimsAsync());

        var reclaimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Equal(2, reclaimed.Count);
        Assert.DoesNotContain(claimed[0].ClipId, reclaimed.Select(c => c.ClipId));

        // The already-embedded clip was not knocked back to pending.
        var coverage = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(1, coverage.Embedded);

        // A sweep with nothing stalled is a no-op.
        await scope.ClipStoreService.ResetStalledEmbeddingClaimsAsync();
        Assert.Equal(0, await scope.ClipStoreService.ResetStalledEmbeddingClaimsAsync());
    }

    [Fact]
    public async Task ReleaseEmbeddingClaims_ReturnsOnlyTheNamedProcessingClips()
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
                ContentText = $"release {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"release {i}"),
                SourceApp = "Editor",
            });
        }

        var claimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Equal(3, claimed.Count);

        await scope.ClipStoreService.ReleaseEmbeddingClaimsAsync([claimed[0].ClipId, claimed[2].ClipId]);

        var reclaimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        var expected = new[] { claimed[0].ClipId, claimed[2].ClipId }.OrderBy(id => id).ToArray();
        Assert.Equal(expected, reclaimed.Select(c => c.ClipId).OrderBy(id => id).ToArray());

        // An empty release is a no-op rather than a malformed "IN ()" query.
        await scope.ClipStoreService.ReleaseEmbeddingClaimsAsync([]);
    }

    [Fact]
    public async Task RebuildSensitivityMatches_ReconcilesEmbeddingStateInBothDirections()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        await scope.SensitivityService.SaveRulesAsync([
            new SensitivityRule { Name = "alpha", Pattern = "alpha", IsEnabled = true },
        ]);
        await scope.SensitivityService.ReloadAsync();

        var secret = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "beta token value",
            ContentBytes = Encoding.UTF8.GetBytes("beta token value"),
            SourceApp = "Editor",
        });
        Assert.NotNull(secret);

        var innocuous = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "alpha harmless note",
            ContentBytes = Encoding.UTF8.GetBytes("alpha harmless note"),
            SourceApp = "Editor",
        });
        Assert.NotNull(innocuous);

        await scope.ClipStoreService.ApplyPendingSensitivityAsync();

        // Capture-time scanning sets is_sensitive but leaves embedding_status NULL.
        // Run the explicit rule-derived classification so the clip really is in the
        // 'excluded' state the rebuild has to undo - and note this is a rule verdict,
        // not a hand mark, so it must NOT survive the rule change.
        await scope.ClipStoreService.ApplySensitivityAsync(innocuous!.Id);

        // "alpha" matched, so that clip is excluded; the other one embeds normally.
        var claimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Equal([secret!.Id], claimed.Select(c => c.ClipId).ToArray());

        var beforeCoverage = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(1, beforeCoverage.Excluded);


        var vec = new float[8];
        for (var k = 0; k < vec.Length; k++) vec[k] = 0.35355339f;
        await scope.ClipStoreService.SaveEmbeddingBatchAsync([new ClipEmbeddingRecord(secret.Id, vec)], "test-model-v1");
        Assert.Single(await scope.ClipStoreService.LoadAllEmbeddingsAsync());

        // Flip the rules: "beta" is now the secret, "alpha" no longer is.
        await scope.SensitivityService.SaveRulesAsync([
            new SensitivityRule { Name = "beta", Pattern = "beta", IsEnabled = true },
        ]);
        await scope.ClipStoreService.RebuildSensitivityMatchesAsync();

        // The clip that just became sensitive must not keep the vector derived
        // from it, and must not still count as embedded.
        Assert.Empty(await scope.ClipStoreService.LoadAllEmbeddingsAsync());

        // The clip that stopped being sensitive must become embeddable again
        // rather than staying stuck in 'excluded'.
        var reclaimed = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.Equal([innocuous!.Id], reclaimed.Select(c => c.ClipId).ToArray());

        var coverage = await scope.ClipStoreService.GetEmbeddingCoverageAsync();
        Assert.Equal(0, coverage.Embedded);
        Assert.Equal(1, coverage.Excluded);
    }

    [Fact]
    public async Task RebuildSensitivityMatches_KeepsHandMarkedClipsSensitiveAndUnembedded()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        await scope.SensitivityService.SaveRulesAsync([
            new SensitivityRule { Name = "alpha", Pattern = "alpha", IsEnabled = true },
        ]);
        await scope.SensitivityService.ReloadAsync();

        var handMarked = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "nothing any rule would ever match",
            ContentBytes = Encoding.UTF8.GetBytes("nothing any rule would ever match"),
            SourceApp = "Editor",
        });
        Assert.NotNull(handMarked);

        await scope.ClipStoreService.ApplyPendingSensitivityAsync();

        // The user marks it sensitive themselves — no rule produced this verdict,
        // so there are no match rows backing it.
        await scope.ClipStoreService.SetSensitiveAsync(handMarked!.Id, true);
        Assert.Empty(await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10));

        // Any rule change triggers a full rebuild. It must not quietly undo the
        // user's own decision, and must certainly not then embed the clip.
        await scope.SensitivityService.SaveRulesAsync([
            new SensitivityRule { Name = "beta", Pattern = "beta", IsEnabled = true },
        ]);
        await scope.ClipStoreService.RebuildSensitivityMatchesAsync();

        var reloaded = await scope.ClipStoreService.GetByIdAsync(handMarked.Id);
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.IsSensitive, "A hand-marked clip must stay sensitive across a sensitivity rule change.");
        Assert.Empty(await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10));
    }

    [Fact]
    public async Task Migration_BackfillsManualSensitivityForClipsUpgradedFromAnOlderSchema()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        await scope.SensitivityService.SaveRulesAsync([
            new SensitivityRule { Name = "alpha", Pattern = "alpha", IsEnabled = true },
        ]);
        await scope.SensitivityService.ReloadAsync();

        var ruleMatched = await CaptureTextAsync(scope, "alpha harmless note");
        var handMarked = await CaptureTextAsync(scope, "nothing any rule would ever match");
        await scope.ClipStoreService.ApplyPendingSensitivityAsync();
        await scope.ClipStoreService.SetSensitiveAsync(handMarked.Id, true);

        // Rewind to the pre-migration schema so the upgrade path runs for real.
        await using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = """
                ALTER TABLE clips DROP COLUMN sensitivity_is_manual;
                UPDATE app_metadata SET value = '5' WHERE key = 'schema_version';
                """;
            await drop.ExecuteNonQueryAsync();
        }

        await scope.DatabaseInitializer.InitializeAsync();

        // The upgrade must not silently discard a mark the user made before it. The
        // match rows are still intact here, which is what makes the two cases
        // distinguishable - that is precisely why the backfill has to happen now.
        await scope.SensitivityService.SaveRulesAsync([
            new SensitivityRule { Name = "beta", Pattern = "beta", IsEnabled = true },
        ]);
        await scope.ClipStoreService.RebuildSensitivityMatchesAsync();

        var reloadedManual = await scope.ClipStoreService.GetByIdAsync(handMarked.Id);
        Assert.NotNull(reloadedManual);
        Assert.True(reloadedManual!.IsSensitive, "A clip hand-marked before the upgrade must survive it.");

        var reloadedRuleMatched = await scope.ClipStoreService.GetByIdAsync(ruleMatched.Id);
        Assert.NotNull(reloadedRuleMatched);
        Assert.False(reloadedRuleMatched!.IsSensitive, "A rule-derived verdict must still be re-derived, not frozen by the backfill.");

        var claimable = await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10);
        Assert.DoesNotContain(handMarked.Id, claimable.Select(c => c.ClipId));
    }

    private static async Task<ClipEntry> CaptureTextAsync(TemporaryDatabaseScope scope, string text)
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

    [Fact]
    public async Task RebuildSensitivityMatches_KeepsImageClipsMatchedOnTheirOcrText()    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 65536 });

        await scope.SensitivityService.SaveRulesAsync([
            new SensitivityRule { Name = "token", Pattern = "sk-live", IsEnabled = true },
        ]);
        await scope.SensitivityService.ReloadAsync();

        var image = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            SourceApp = "Snipper",
        });
        Assert.NotNull(image);

        await scope.ClipStoreService.ApplyPendingSensitivityAsync();

        // OCR recognises a secret in the screenshot and marks it sensitive.
        Assert.True(await scope.ClipStoreService.TryClaimForOcrAsync(image!.Id));
        Assert.True(await scope.ClipStoreService.SetOcrResultAsync(image.Id, "credential sk-live-9182"));

        var afterOcr = await scope.ClipStoreService.GetByIdAsync(image.Id);
        Assert.True(afterOcr!.IsSensitive);

        // A rebuild scanning only `content` would find nothing for an image clip
        // and silently declassify it.
        await scope.ClipStoreService.RebuildSensitivityMatchesAsync();

        var afterRebuild = await scope.ClipStoreService.GetByIdAsync(image.Id);
        Assert.True(afterRebuild!.IsSensitive, "An image clip whose OCR text matches a rule must stay sensitive across a rebuild.");
        Assert.Empty(await scope.ClipStoreService.ClaimPendingEmbeddingsAsync(10));
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

    /// <summary>
    /// A search term shorter than the trigram threshold - the first two keystrokes of
    /// every search - cannot use the FTS index and falls back to a scan. The scan used to
    /// build a full thirty-column ClipEntry for every row, five timestamp parses included,
    /// and then throw almost all of them away; at 100k rows that was ~70% of a 777 ms
    /// query. Matching now runs off the reader and only a surviving row is materialized.
    ///
    /// Allocation is the observable that separates the two shapes. Wall-clock would turn a
    /// real regression into a flake on a loaded machine, and result assertions pass just as
    /// happily against the wasteful version.
    /// </summary>
    [Fact]
    public async Task NonFtsSearch_DoesNotMaterializeRowsItDiscards()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });

        const int rows = 4_000;
        var random = new Random(31);
        var words = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot" };
        var batch = new List<ClipCaptureRequest>(500);
        for (var i = 0; i < rows; i++)
        {
            var builder = new StringBuilder(256);
            for (var word = 0; word < 40; word++)
            {
                builder.Append(words[random.Next(words.Length)]).Append(' ');
            }

            var text = builder.Append(i).ToString();
            batch.Add(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = text,
                ContentBytes = Encoding.UTF8.GetBytes(text),
                SourceApp = "App" + (i % 20),
                SkipPostInsertMaintenance = true,
            });

            if (batch.Count == 500)
            {
                await scope.ClipStoreService.CaptureBatchAsync(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await scope.ClipStoreService.CaptureBatchAsync(batch);
        }

        // Two characters, so the trigram index cannot serve it, and a pair that appears in
        // none of the seeded text, so every row is read and discarded - the worst case, and
        // exactly what typing a term that does not exist yet produces.
        var filters = new ClipSearchFilters { SearchText = "zq", Limit = 100 };

        // Warm every lazy path (statement prepare, stats cache) so the measured run only
        // covers the scan.
        var warm = await scope.ClipStoreService.SearchAsync(filters);
        Assert.Empty(warm.Items);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var result = await scope.ClipStoreService.SearchAsync(filters);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalMatchingCount);

        // Reading the content of every row is unavoidable - that is the match. Building an
        // entry for each of them is not. Measured at ~2.6 MB with the fix and ~7 MB
        // without, so this budget separates the two with room for allocator noise.
        const long budget = 4L * 1024 * 1024;
        Assert.True(
            allocated < budget,
            $"scan allocated {allocated / 1024.0 / 1024.0:F1} MB over {rows} discarded rows, above the {budget / 1024 / 1024} MB budget - non-matching rows are being materialized");
    }

    /// <summary>
    /// A non-FTS search reads five columns off the reader by ordinal and only builds a
    /// ClipEntry once they match, so the ordinals are now load-bearing: reorder the SELECT
    /// list and the search silently starts matching a different column instead of failing.
    /// Each clip below carries the term in exactly one searched column, plus one that
    /// carries it only in a column the search deliberately does not cover.
    /// A two-character term is used because it is not FTS-compatible against a trigram
    /// index, which is what routes the query through the scan under test.
    /// </summary>
    [Fact]
    public async Task NonFtsSearch_CoversExactlyTheFiveSearchedColumns()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 4096 });

        // Two characters: below the trigram threshold, so this cannot go through FTS.
        const string term = "qx";
        Assert.Equal(2, term.Length);

        async Task<long> SeedAsync(ClipCaptureRequest request)
        {
            var clip = await scope.ClipStoreService.CaptureAsync(request);
            Assert.NotNull(clip);
            return clip!.Id;
        }

        var inContent = await SeedAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "carries qx in the body",
            ContentBytes = Encoding.UTF8.GetBytes("carries qx in the body"),
        });

        var inSourceApp = await SeedAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "plain body one",
            ContentBytes = Encoding.UTF8.GetBytes("plain body one"),
            SourceApp = "qx-editor",
        });

        var inWindowTitle = await SeedAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "plain body two",
            ContentBytes = Encoding.UTF8.GetBytes("plain body two"),
            SourceWindowTitle = "a qx window",
        });

        var inSourceUrl = await SeedAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "plain body three",
            ContentBytes = Encoding.UTF8.GetBytes("plain body three"),
            SourceUrl = "https://example.test/qx",
        });

        var inOcrText = await SeedAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentText = string.Empty,
            ContentBytes = [1, 2, 3, 4],
        });
        Assert.True(await scope.ClipStoreService.SetOcrResultAsync(inOcrText, "scanned qx text"));

        // The search covers five columns. source_app_path is not one of them, and a clip
        // that carries the term only there must stay out of the results - otherwise an
        // ordinal pointing one column off would look like a pass.
        var inUnsearchedColumn = await SeedAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "plain body four",
            ContentBytes = Encoding.UTF8.GetBytes("plain body four"),
            SourceApp = "Editor",
            SourceAppPath = @"C:\qx\editor.exe",
        });

        var result = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = term, Limit = 50 });
        var found = result.Items.Select(item => item.Id).ToHashSet();

        Assert.Contains(inContent, found);
        Assert.Contains(inSourceApp, found);
        Assert.Contains(inWindowTitle, found);
        Assert.Contains(inSourceUrl, found);
        Assert.Contains(inOcrText, found);
        Assert.DoesNotContain(inUnsearchedColumn, found);
        Assert.Equal(5, result.TotalMatchingCount);
    }

    /// <summary>
    /// The list has to fetch icons back for nearly every visible row (U12 omits the blob).
    /// Doing that through GetByIdAsync pulled all thirty columns including the image blob,
    /// so a page of clips dragged megabytes of unrelated data across the wire. This is the
    /// narrow read that replaced it.
    /// </summary>
    [Fact]
    public async Task GetSourceAppIconAsync_ReturnsTheIconWithoutTheRestOfTheRow()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 1_048_576 });

        var imageBytes = new byte[4096];
        new Random(7).NextBytes(imageBytes);
        var iconBytes = new byte[256];
        new Random(8).NextBytes(iconBytes);

        var withIcon = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentBytes = imageBytes,
            SourceAppIconBytes = iconBytes,
            ImageWidth = 16,
            ImageHeight = 16,
        });

        var withoutIcon = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = "no icon here",
            ContentBytes = Encoding.UTF8.GetBytes("no icon here"),
        });

        Assert.Equal(iconBytes, await scope.ClipStoreService.GetSourceAppIconAsync(withIcon!.Id));
        Assert.Null(await scope.ClipStoreService.GetSourceAppIconAsync(withoutIcon!.Id));
        Assert.Null(await scope.ClipStoreService.GetSourceAppIconAsync(999_999));
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

    /// <summary>
    /// Regression test for B2d (part 1): SearchInMemoryAsync hardcoded the
    /// MostRecent ORDER BY instead of honouring filters.SortOption. Because
    /// SearchAsync diverts to that path whenever the search uses regex, case
    /// sensitivity, wildcards or whole-word matching, the sort dropdown
    /// silently stopped working the moment the user ticked one of those boxes -
    /// which is exactly the user who cares about ordering.
    ///
    /// The oracle is the no-search SQL path, which has always honoured
    /// SortOption. Both paths see the same set of clips here, so for any given
    /// sort they must return the same sequence.
    /// </summary>
    [Theory]
    [InlineData(ClipSortOption.MostRecent)]
    [InlineData(ClipSortOption.OldestFirst)]
    [InlineData(ClipSortOption.MostPasted)]
    [InlineData(ClipSortOption.Alphabetical)]
    [InlineData(ClipSortOption.LargestFirst)]
    [InlineData(ClipSortOption.BestMatching)]
    public async Task SearchAsync_InMemoryPathHonoursTheSelectedSort(ClipSortOption sortOption)
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 65536 });

        await SeedSortableClipsAsync(scope);

        // No search text -> SQL path, which is the reference implementation.
        var reference = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters
        {
            SortOption = sortOption,
        });

        // CaseSensitive forces the in-memory path. Every seeded clip contains
        // the lowercase marker, so both paths match the identical set and any
        // difference in the returned sequence is a difference in ordering.
        var inMemory = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters
        {
            SearchText = "zulu",
            CaseSensitive = true,
            SortOption = sortOption,
        });

        Assert.Equal(reference.Items.Count, inMemory.Items.Count);
        Assert.Equal(
            reference.Items.Select(item => item.Id).ToArray(),
            inMemory.Items.Select(item => item.Id).ToArray());
    }

    /// <summary>
    /// Guards <see cref="SearchAsync_InMemoryPathHonoursTheSelectedSort"/>
    /// against becoming vacuous. That test compares two orderings, so it only
    /// proves anything if the fixture actually orders differently under each
    /// sort. If a future change to the seed data made two sorts agree, the
    /// theory above would pass for one of them no matter how broken the
    /// ordering code was. This caught a real fixture mistake once already.
    /// </summary>
    [Fact]
    public async Task SortableClipFixture_ProducesADistinctOrderForEverySortKey()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 65536 });

        await SeedSortableClipsAsync(scope);

        async Task<string> OrderFor(ClipSortOption sortOption)
        {
            var result = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SortOption = sortOption });
            return string.Join(",", result.Items.Select(item => item.Id));
        }

        // BestMatching is excluded: with no search term it intentionally
        // resolves to the same clause as MostRecent.
        ClipSortOption[] distinctSorts =
        [
            ClipSortOption.MostRecent,
            ClipSortOption.OldestFirst,
            ClipSortOption.MostPasted,
            ClipSortOption.Alphabetical,
            ClipSortOption.LargestFirst,
        ];

        var orders = new List<string>();
        foreach (var sortOption in distinctSorts)
        {
            orders.Add($"{sortOption}={await OrderFor(sortOption)}");
        }

        var sequences = orders.Select(entry => entry[(entry.IndexOf('=') + 1)..]).ToArray();
        Assert.Equal(distinctSorts.Length, sequences.Distinct().Count());
    }

    /// <summary>
    /// Seeds four clips that sort differently under every supported sort key,
    /// including a pinned one so the pinned-first prefix shared by every
    /// ORDER BY clause is exercised too.
    /// </summary>
    private static async Task SeedSortableClipsAsync(TemporaryDatabaseScope scope)
    {
        async Task<ClipEntry> Capture(string text, int extraBytes)
        {
            var padded = text + new string('x', extraBytes);
            var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = padded,
                ContentBytes = Encoding.UTF8.GetBytes(padded),
            });

            Assert.NotNull(clip);

            // captured_at has second-level granularity in some paths; sleep so
            // the recency ordering is unambiguous rather than tie-broken by id.
            await Task.Delay(15);
            return clip!;
        }

        // Captured oldest to newest. Content, size and paste count are each
        // arranged to disagree with capture order AND with each other, so no
        // two sorts produce the same sequence. Sizes in particular must not
        // track recency: the first attempt made the largest clip the most
        // recently pasted one, which made LargestFirst and MostRecent
        // indistinguishable and the comparison vacuous.
        var lastPasted = await Capture("zulu ccc", extraBytes: 100);
        var mostPasted = await Capture("zulu aaa", extraBytes: 0);
        await Capture("zulu bbb", extraBytes: 400);
        var pinned = await Capture("zulu ddd", extraBytes: 50);

        // MarkPastedAsync also bumps last_copied_at, so the clip pasted most
        // often deliberately is not the clip pasted most recently.
        await scope.ClipStoreService.MarkPastedAsync(mostPasted.Id);
        await scope.ClipStoreService.MarkPastedAsync(mostPasted.Id);
        await scope.ClipStoreService.MarkPastedAsync(lastPasted.Id);
        await scope.ClipStoreService.SetPinnedAsync(pinned.Id, true);
    }
    /// <summary>
    /// Alphabetical now orders by a bounded content prefix first so it can be
    /// served from an index. That rewrite is only safe if it is truly
    /// order-equivalent to ordering by the whole content, so this pins it
    /// against the naive clause using inputs designed to break a prefix
    /// comparison: strings sharing more than the indexed 64 characters, one
    /// string that is a strict prefix of another, empty content, and content
    /// differing only past the prefix boundary.
    /// </summary>
    [Fact]
    public async Task Alphabetical_OrdersIdenticallyToOrderingByWholeContent()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        var shared = new string('a', 64);
        var texts = new[]
        {
            shared + "zzz",
            shared + "aaa",
            shared,
            shared + "aaa" + "b",
            "b",
            "a",
            string.Empty,
            new string('a', 63) + "b",
            new string('a', 65),
            shared + "\u00e9",
            shared + "Z",

            // "Beta" and "apple" invert between BINARY and NOCASE. The oracle
            // here compares whole content under NOCASE, so this pair fails the
            // moment the prefix term and the tie-break term stop agreeing on a
            // collation - which is exactly when the index stops serving them.
            "Beta",
            "apple",
        };

        foreach (var text in texts)
        {
            var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = text,
                ContentBytes = Encoding.UTF8.GetBytes(text),
            });

            // Empty content may be rejected by capture; that is fine, the rest
            // of the fixture still exercises the boundary.
            _ = clip;
        }

        var actual = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters
        {
            SortOption = ClipSortOption.Alphabetical,
        });

        // The oracle is the clause this replaced, run directly against SQLite.
        var expected = new List<long>();
        await using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.id
                FROM clips c
                ORDER BY (c.pinned_at IS NULL), c.pinned_at DESC, c.content COLLATE NOCASE ASC, c.id ASC;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                expected.Add(reader.GetInt64(0));
            }
        }

        // Guard: a fixture that collapsed to one row would prove nothing.
        Assert.True(expected.Count >= 8, $"Fixture too small: {expected.Count} clips.");
        Assert.Equal(expected, actual.Items.Select(item => item.Id).ToList());
    }

    /// <summary>
    /// Every sort must be servable straight from an index. A missing index does
    /// not merely lose its own sort - SQLite picks some other pinned-prefixed
    /// index and then fetches rows in an order uncorrelated with the table,
    /// which measured roughly twice as slow as having no index at all. So the
    /// index set has to stay complete, and a query plan is a far more stable
    /// assertion than a stopwatch.
    /// </summary>
    [Theory]
    [InlineData(ClipSortOption.MostRecent, "idx_clips_default_order")]
    [InlineData(ClipSortOption.OldestFirst, "idx_clips_oldest_order")]
    [InlineData(ClipSortOption.MostPasted, "idx_clips_paste_order")]
    [InlineData(ClipSortOption.LargestFirst, "idx_clips_size_order")]
    // Alphabetical is deliberately absent: its plan cannot defend it. See
    // Alphabetical_OrdersByTheExpressionItsIndexStores.
    public async Task EverySort_IsServedFromItsOwnIndex(ClipSortOption sortOption, string expectedIndex)
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedSortableClipsAsync(scope);

        var text = await ExplainOrderPlanAsync(scope, sortOption);

        Assert.Contains(expectedIndex, text, StringComparison.Ordinal);
        Assert.DoesNotContain("TEMP B-TREE", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Retention purging runs after every capture and, in the normal case, deletes
    /// nothing at all - so what it costs when it finds nothing is what it costs.
    /// Unindexed, both lifetime deletes scan the whole table: maintenance measured
    /// 110ms per capture at 100k clips with nothing to purge, against 25ms once
    /// this index existed.
    ///
    /// The plan is asserted against the production statement itself. A copy of the
    /// SQL would keep passing after the predicate stopped matching the index - and
    /// the failure mode is silent, since the delete still returns the right rows.
    /// </summary>
    [Fact]
    public async Task RetentionPurge_IsServedFromItsOwnIndex()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedSortableClipsAsync(scope);

        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + ClipStoreService.RetentionDeleteStatement;
        command.Parameters.AddWithValue("$isSensitive", 0);
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$keepUserKept", 1);

        var text = new StringBuilder();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                text.AppendLine(reader.GetString(3));
            }
        }

        var plan = text.ToString();

        // Naming the index is not enough. SQLite reports "SEARCH clips USING INDEX
        // idx_clips_retention" as soon as the is_sensitive equality prefix is usable,
        // even when the date range - the part that actually costs - still walks every
        // row on that side of the index. Only the range term proves otherwise.
        Assert.Contains("idx_clips_retention (is_sensitive=? AND last_copied_at<?)", plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// Alphabetical keeps a TEMP B-TREE for the terms that break prefix ties, so
    /// it cannot join the theory above. It still has to be *served* from its own
    /// index: with that index missing SQLite grabs another pinned-prefixed one
    /// and then fetches rows in an order uncorrelated with the table, which
    /// measured ~2x slower than having no index at all (206ms vs 118ms at 20k
    /// clips). Asserting only that the clause and the index agree does not cover
    /// this - an index can be perfectly well-formed and still not be chosen.
    /// </summary>
    [Fact]
    public async Task Alphabetical_IsServedFromItsOwnIndex()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedSortableClipsAsync(scope);

        var text = await ExplainOrderPlanAsync(scope, ClipSortOption.Alphabetical);

        Assert.Contains("idx_clips_alpha_order_ci", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Alphabetical" has to mean what a reader of a dictionary means by it.
    /// Under SQLite's default BINARY collation it did not: uppercase codepoints
    /// all precede lowercase ones, so every capitalised clip was banished ahead
    /// of every lowercase one and "apple" sorted after "Zebra". The equivalence
    /// test above cannot catch this - its oracle is the same clause - so assert
    /// the user-visible order directly.
    /// </summary>
    [Fact]
    public async Task Alphabetical_IgnoresCaseSoCapitalisedClipsAreNotBanishedToTheFront()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        foreach (var text in new[] { "Zebra", "apple", "Banana", "cherry" })
        {
            await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = text,
                ContentBytes = Encoding.UTF8.GetBytes(text),
            });
        }

        var result = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters
        {
            SortOption = ClipSortOption.Alphabetical,
        });

        Assert.Equal(
            new[] { "apple", "Banana", "cherry", "Zebra" },
            result.Items.Select(item => item.Content).ToArray());
    }

    private static async Task<string> ExplainOrderPlanAsync(TemporaryDatabaseScope scope, ClipSortOption sortOption)
    {
        var plan = new List<string>();
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN SELECT c.id FROM clips c {ClipStoreService.BuildOrderClause(sortOption)} LIMIT 200;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }

        return string.Join(" | ", plan);
    }
    /// <summary>
    /// Alphabetical is the one sort whose query plan proves nothing. With
    /// idx_clips_alpha_order present, the old whole-content clause and the
    /// prefix clause produce a byte-identical plan string - the index supplies
    /// the two pinned columns either way, and only the volume the sorter has to
    /// swallow differs. A full revert of the clause therefore passes both the
    /// plan test and the equivalence test (whose oracle IS the old clause), so
    /// neither can defend the optimisation. Pin the clause to the index
    /// definition structurally instead: the ORDER BY must sort by exactly the
    /// expression the index stores, or the index cannot serve it.
    /// </summary>
    [Fact]
    public async Task Alphabetical_OrdersByTheExpressionItsIndexStores()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        string? indexSql;
        await using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'idx_clips_alpha_order_ci';";
            indexSql = await command.ExecuteScalarAsync() as string;
        }

        Assert.False(string.IsNullOrEmpty(indexSql), "idx_clips_alpha_order_ci is missing.");

        static string Normalise(string sql) => sql
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("c.", string.Empty, StringComparison.Ordinal);

        // The collation is part of the expression for this purpose: an index is
        // only usable by an ORDER BY that compares with the same one, so a
        // clause that dropped COLLATE NOCASE would silently stop being served.
        var indexed = System.Text.RegularExpressions.Regex.Match(Normalise(indexSql!), @"substr\([^)]*\)(COLLATE\w+)?");
        Assert.True(indexed.Success, $"No substr expression in the index: {indexSql}");
        Assert.Contains("COLLATE", indexed.Value, StringComparison.Ordinal);

        var clause = Normalise(ClipStoreService.BuildOrderClause(ClipSortOption.Alphabetical));
        Assert.Contains(indexed.Value, clause, StringComparison.Ordinal);
    }
}
