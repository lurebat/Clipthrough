using System;
using System.Linq;
using System.Threading.Tasks;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// U-D5 part 2 (F-10). List and search rows carry only the first
/// <see cref="ClipEntry.ListContentLength"/> characters of a clip's text. That
/// bound is safe only if two things hold, and these tests pin both: a row says
/// when it is truncated and refuses to hand out the prefix as the clip, and every
/// search engine still decides a match on the whole text, not on what the row
/// carries.
/// </summary>
public sealed class BoundedListTextTests
{
    private const string Needle = "zebra-quokka";

    // The needle sits well past the bound, so a match can only come from the
    // whole text. Plain words so the FTS trigram index has something to work on.
    private static readonly string LongText =
        string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet ", 400)) + Needle + " tail";

    private static async Task<(TemporaryDatabaseScope Scope, long Id)> SeedAsync(string text)
    {
        var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
        {
            ContentBytes = System.Text.Encoding.UTF8.GetBytes(text),
            ContentText = text,
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            SourceApp = "Tests",
        });
        return (scope, clip!.Id);
    }

    [Fact]
    public async Task ALongClipsListRow_CarriesOnlyAPrefix_AndRefusesToPassItOffAsTheClip()
    {
        Assert.True(LongText.Length > 2 * ClipEntry.ListContentLength, "Setup: the clip must be longer than the bound.");
        var (scope, id) = await SeedAsync(LongText);
        using var _ = scope;

        var row = Assert.Single((await scope.ClipStoreService.SearchAsync(new ClipSearchFilters())).Items);

        Assert.True(row.IsContentTruncated);
        Assert.Equal(LongText[..ClipEntry.ListContentLength], row.ContentPrefix);
        Assert.Throws<InvalidOperationException>(() => row.Content);

        var full = await scope.ClipStoreService.GetByIdAsync(id);
        Assert.False(full!.IsContentTruncated);
        Assert.Equal(LongText, full.Content);
    }

    /// <summary>
    /// The Transform menus, toolbar and row context menu ask
    /// <see cref="Clipthrough.Services.TextTransformationService.CanTransform"/> about the list
    /// row, not the hydrated clip. Reading <see cref="ClipEntry.Content"/> there
    /// threw for every clip longer than the bound (Phase B's gate meeting Phase
    /// D's bounded rows), so a long clip could not be transformed at all.
    /// </summary>
    [Fact]
    public async Task ALongClipsListRow_CanStillBeTransformed()
    {
        var (scope, _) = await SeedAsync(LongText);
        using var __ = scope;

        var row = Assert.Single((await scope.ClipStoreService.SearchAsync(new ClipSearchFilters())).Items);
        Assert.True(row.IsContentTruncated, "Setup: the row must be a truncated one.");

        Assert.True(Clipthrough.Services.TextTransformationService.CanTransform(row));
    }

    [Fact]
    public async Task AClipThatFits_IsNotTruncated()
    {
        var text = new string('x', ClipEntry.ListContentLength);
        var (scope, _) = await SeedAsync(text);
        using var __ = scope;

        var row = Assert.Single((await scope.ClipStoreService.SearchAsync(new ClipSearchFilters())).Items);

        Assert.False(row.IsContentTruncated);
        Assert.Equal(text, row.Content);
    }

    public static TheoryData<string, ClipSearchFilters> EveryEngine() => new()
    {
        { "fts", new ClipSearchFilters { SearchText = Needle } },
        { "regex", new ClipSearchFilters { SearchText = "zebra-q.okka", UseRegex = true } },
        { "case-sensitive", new ClipSearchFilters { SearchText = Needle, CaseSensitive = true } },
        { "whole-word", new ClipSearchFilters { SearchText = "quokka", WholeWord = true } },
        { "wildcard", new ClipSearchFilters { SearchText = "zebra*kka", UseWildcard = true } },
    };

    /// <summary>
    /// The owner's condition for bounding the read: search and filter match the
    /// full text. The in-memory engines (regex, case, whole word, wildcard) read
    /// the row to decide, so they are the ones a bounded read could blind.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryEngine))]
    public async Task ATermPastTheBound_IsFound_ByEverySearchEngine(string engine, ClipSearchFilters filters)
    {
        var (scope, id) = await SeedAsync(LongText);
        using var _ = scope;

        var result = await scope.ClipStoreService.SearchAsync(filters);

        var row = Assert.Single(result.Items);
        Assert.True(row.Id == id, $"{engine}: found the wrong clip");
        Assert.True(row.IsContentTruncated, $"{engine}: the matching row should still carry only a prefix");
    }
}
