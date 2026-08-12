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

            // A BINARY/NOCASE inversion: under BINARY "Beta" precedes "apple"
            // (uppercase sorts first), under NOCASE it does not. If anyone
            // gives content a different collation, the prefix term and the
            // tie-break term would stop agreeing, and this pair is what
            // notices.
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
                ORDER BY (c.pinned_at IS NULL), c.pinned_at DESC, c.content ASC, c.id ASC;
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

        Assert.Contains("idx_clips_alpha_order", text, StringComparison.Ordinal);
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
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'idx_clips_alpha_order';";
            indexSql = await command.ExecuteScalarAsync() as string;
        }

        Assert.False(string.IsNullOrEmpty(indexSql), "idx_clips_alpha_order is missing.");

        static string Normalise(string sql) => sql
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("c.", string.Empty, StringComparison.Ordinal);

        var indexed = System.Text.RegularExpressions.Regex.Match(Normalise(indexSql!), @"substr\([^)]*\)");
        Assert.True(indexed.Success, $"No substr expression in the index: {indexSql}");

        var clause = Normalise(ClipStoreService.BuildOrderClause(ClipSortOption.Alphabetical));
        Assert.Contains(indexed.Value, clause, StringComparison.Ordinal);
    }
}
