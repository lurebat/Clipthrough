using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// <see cref="ClipStructuralFilter"/> is a second expression of the structural
/// half of <c>ClipStoreService.BuildWhereClauses</c>, which exists so the UI can
/// decide whether a newly captured clip belongs in the list the user is looking
/// at without a database round trip. Duplicated rules drift, so these tests do
/// not check the predicate against hand-written expectations - they check it
/// against SQLite itself, over the full cross product of structural filters.
/// </summary>
public sealed class ClipStructuralFilterTests
{
    /// <summary>
    /// Synthetic, obviously-fake credential text. Not a real secret; it exists
    /// only because it matches the built-in "Passwords" rule, which is how a
    /// clip gets flagged sensitive here.
    /// </summary>
    private const string SyntheticSecretText = "password = NOT-A-REAL-CREDENTIAL";

    [Fact]
    public async Task Matches_AgreesWithTheDatabaseForEveryStructuralFilterCombination()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        scope.SettingsService.SetCurrent(new AppSettings { MaxClipSizeBytes = 65536 });

        var clips = await SeedVariedClipsAsync(scope);

        IReadOnlyCollection<ContentType>?[] contentTypeSets =
        [
            null,
            [ContentType.Text],
            [ContentType.Image],
            [ContentType.Text, ContentType.Image],
        ];

        var combinationsChecked = 0;
        var nonEmptyResults = 0;

        foreach (var contentTypes in contentTypeSets)
        {
            foreach (var favoritesOnly in new[] { false, true })
            {
                foreach (var sensitiveOnly in new[] { false, true })
                {
                    foreach (var pastedOnly in new[] { false, true })
                    {
                        var filters = new ClipSearchFilters
                        {
                            ContentTypes = contentTypes,
                            FavoritesOnly = favoritesOnly,
                            SensitiveOnly = sensitiveOnly,
                            PastedOnly = pastedOnly,
                        };

                        var fromDatabase = await scope.ClipStoreService.SearchAsync(filters);
                        var expected = fromDatabase.Items.Select(item => item.Id).OrderBy(id => id).ToArray();

                        var fromPredicate = clips
                            .Where(clip => ClipStructuralFilter.Matches(filters, clip))
                            .Select(clip => clip.Id)
                            .OrderBy(id => id)
                            .ToArray();

                        Assert.Equal(expected, fromPredicate);

                        combinationsChecked++;
                        if (expected.Length > 0)
                        {
                            nonEmptyResults++;
                        }
                    }
                }
            }
        }

        Assert.Equal(32, combinationsChecked);

        // Anti-vacuity: a predicate that always returned false would agree with
        // the database on every combination that legitimately matches nothing.
        // The fixture must produce plenty of non-empty result sets.
        Assert.True(nonEmptyResults >= 16, $"Expected most combinations to match something, but only {nonEmptyResults} did.");
    }

    /// <summary>
    /// Search text is deliberately outside the predicate's remit, because
    /// reproducing FTS tokenisation in memory is not something a caller can do
    /// correctly. This pins that boundary so nobody later "helpfully" starts
    /// honouring SearchText here and makes the predicate silently wrong.
    /// </summary>
    [Fact]
    public void Matches_IgnoresSearchText()
    {
        var clip = new ClipEntry { Id = 1, Content = "hello", ContentType = ContentType.Text };
        var filters = new ClipSearchFilters { SearchText = "definitely not present" };

        Assert.True(ClipStructuralFilter.Matches(filters, clip));
    }

    private static async Task<List<ClipEntry>> SeedVariedClipsAsync(TemporaryDatabaseScope scope)
    {
        var results = new List<ClipEntry>();

        async Task<ClipEntry> CaptureText(string text)
        {
            var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = text,
                ContentBytes = Encoding.UTF8.GetBytes(text),
            });

            Assert.NotNull(clip);
            return clip!;
        }

        var plain = await CaptureText("plain text clip");
        var favorite = await CaptureText("favourite text clip");
        var pasted = await CaptureText("pasted text clip");
        var secret = await CaptureText(SyntheticSecretText);

        var image = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Image,
            ContentFormat = ClipContentFormat.Bitmap,
            ContentText = string.Empty,
            ContentBytes = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4],
            ImageWidth = 640,
            ImageHeight = 480,
        });
        Assert.NotNull(image);

        var richText = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.RichText,
            ContentFormat = ClipContentFormat.Html,
            ContentText = "<p>rich</p>",
            ContentBytes = Encoding.UTF8.GetBytes("<p>rich</p>"),
        });
        Assert.NotNull(richText);

        await scope.ClipStoreService.SetFavoriteAsync(favorite.Id, true);
        await scope.ClipStoreService.MarkPastedAsync(pasted.Id);

        // An image that is favourite AND pasted, so combined filters are
        // exercised rather than only one flag at a time.
        await scope.ClipStoreService.SetFavoriteAsync(image!.Id, true);
        await scope.ClipStoreService.MarkPastedAsync(image.Id);

        results.AddRange([plain, favorite, pasted, secret, image, richText!]);

        // Re-read every clip so the in-memory copies carry the flags that were
        // applied after capture. Comparing stale entries against fresh database
        // rows would test nothing.
        var refreshed = new List<ClipEntry>(results.Count);
        foreach (var clip in results)
        {
            var current = await scope.ClipStoreService.GetByIdAsync(clip.Id);
            Assert.NotNull(current);
            refreshed.Add(current!);
        }

        // Guard the fixture itself: the cross product is only meaningful if
        // every structural dimension actually varies.
        Assert.Contains(refreshed, clip => clip.IsFavorite);
        Assert.Contains(refreshed, clip => !clip.IsFavorite);
        Assert.Contains(refreshed, clip => clip.IsPasted);
        Assert.Contains(refreshed, clip => !clip.IsPasted);
        Assert.Contains(refreshed, clip => clip.IsSensitive);
        Assert.Contains(refreshed, clip => !clip.IsSensitive);
        Assert.Contains(refreshed, clip => clip.ContentType == ContentType.Text);
        Assert.Contains(refreshed, clip => clip.ContentType == ContentType.Image);
        Assert.Contains(refreshed, clip => clip.ContentType == ContentType.RichText);

        return refreshed;
    }
}
