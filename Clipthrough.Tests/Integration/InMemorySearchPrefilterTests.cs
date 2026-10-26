using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// F-07. Case-sensitive, whole-word and wildcard searches read and test every
/// clip: 2.6-3.2 s and ~250 MB per refresh at 60k clips, 15.9 s encrypted. The
/// trigram index is a superset of all three when every term is three code
/// points or longer, so it now picks the candidates and the exact matcher
/// decides. Expectations are written by hand, not taken from the old path.
/// </summary>
public sealed class InMemorySearchPrefilterTests
{
    private static async Task<long[]> SeedAsync(TemporaryDatabaseScope scope)
    {
        await scope.DatabaseInitializer.InitializeAsync();
        var rows = new (string Content, string? App, string? Ocr)[]
        {
            ("Invoice 2024 paid", null, null),          // 1
            ("invoice due tomorrow", null, null),       // 2
            ("INVOICES archive", null, null),           // 3
            ("reinvoiced items", null, null),           // 4
            ("unrelated text", "InvoiceTool.exe", null),// 5
            ("nothing here", null, "Invoice scanned"),  // 6
            ("invoices due later", null, null),         // 7
        };
        var ids = new long[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = rows[i].Content,
                ContentBytes = Encoding.UTF8.GetBytes(rows[i].Content),
                SourceApp = rows[i].App,
                SkipPostInsertMaintenance = true,
            });
            ids[i] = clip!.Id;
            if (rows[i].Ocr is { } ocr)
            {
                await using var connection = scope.ConnectionFactory.CreateConnection();
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE clips SET ocr_text = $o WHERE id = $id;";
                command.Parameters.AddWithValue("$o", ocr);
                command.Parameters.AddWithValue("$id", clip.Id);
                await command.ExecuteNonQueryAsync();
            }
        }

        return ids;
    }

    public static TheoryData<string, bool, bool, bool, int[]> Cases => new()
    {
        // text, caseSensitive, wholeWord, wildcard, expected row numbers (1-based)
        { "Invoice", true, false, false, [1, 5, 6] },
        { "invoice due", true, false, false, [2, 7] },
        { "invoice", false, true, false, [1, 2, 6] },
        { "Invoice", true, true, false, [1, 6] },
        { "inv*due", false, false, true, [2, 7] },
        { "INVOICES arc*", true, false, true, [3] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PrefilteredSearch_ReturnsExactlyTheHandWrittenMatches(string text, bool caseSensitive, bool wholeWord, bool wildcard, int[] expectedRows)
    {
        using var scope = new TemporaryDatabaseScope();
        var ids = await SeedAsync(scope);
        var filters = new ClipSearchFilters { SearchText = text, CaseSensitive = caseSensitive, WholeWord = wholeWord, UseWildcard = wildcard, Limit = 50 };
        Assert.NotNull(ClipStoreService.BuildPrefilterExpression(filters));

        var result = await scope.ClipStoreService.SearchAsync(filters);

        Assert.Equal(expectedRows.Select(r => ids[r - 1]).Order(), result.Items.Select(i => i.Id).Order());
        Assert.Equal(expectedRows.Length, result.TotalMatchingCount);
    }

    [Theory]
    [InlineData("Invoice", true, false, false, "\"Invoice\"")]
    [InlineData("invoice due", false, true, false, "\"invoice\" AND \"due\"")]
    [InlineData("inv*due?x", false, false, true, null)]
    [InlineData("inv*due", false, false, true, "\"inv\" AND \"due\"")]
    [InlineData("say \"hi\" now", true, false, false, "\"say\" AND \"\"\"hi\"\"\" AND \"now\"")]
    public void ThePrefilter_IsTheTrigramSupersetOfTheTerms(string text, bool caseSensitive, bool wholeWord, bool wildcard, string? expected)
        => Assert.Equal(expected, ClipStoreService.BuildPrefilterExpression(
            new ClipSearchFilters { SearchText = text, CaseSensitive = caseSensitive, WholeWord = wholeWord, UseWildcard = wildcard }));

    [Theory]
    [InlineData("ab", true, false, false)]           // shorter than a trigram
    [InlineData("\U0001F600a", true, false, false)]  // 3 UTF-16 units, 2 code points
    [InlineData("in*v", false, false, true)]         // a wildcard run shorter than a trigram
    public void NoPrefilter_WhenATermIsShorterThanATrigram(string text, bool caseSensitive, bool wholeWord, bool wildcard)
        => Assert.Null(ClipStoreService.BuildPrefilterExpression(
            new ClipSearchFilters { SearchText = text, CaseSensitive = caseSensitive, WholeWord = wholeWord, UseWildcard = wildcard }));

    [Fact]
    public void NoPrefilter_ForRegex()
        => Assert.Null(ClipStoreService.BuildPrefilterExpression(new ClipSearchFilters { SearchText = "invoice", UseRegex = true }));
}