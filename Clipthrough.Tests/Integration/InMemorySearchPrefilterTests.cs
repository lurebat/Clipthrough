using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
    [InlineData("invoice due", false, true, false, "\"nvo\" AND \"due\"")]
    [InlineData("inv*due?x", false, false, true, null)]
    [InlineData("inv*due", false, false, true, "\"due\"")]
    [InlineData("say \"hi\" now", true, false, false, "\"say\" AND \"\"\"hi\"\"\" AND \"now\"")]
    [InlineData("FILE", true, true, false, "\"FILE\"")]
    [InlineData("configuration", false, true, false, "\"conf\" AND \"gurat\"")]
    [InlineData("caf\u00E9 bar", false, true, false, "\"caf\" AND \"bar\"")]
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

    /// <summary>
    /// Rubber-duck F-07-3. A case-insensitive whole-word or wildcard search is
    /// decided by a .NET regex with IgnoreCase, in the current culture. Its case
    /// equivalences are wider than the trigram index's case folding: I and i
    /// also match U+0130 (dotted capital I) in every culture, I matches U+0131
    /// (dotless i) in Turkish and Azerbaijani, and letters added to Unicode after
    /// SQLite's folding table (Georgian Mtavruli here) do not fold at all. The
    /// prefilter dropped these rows before the matcher, which accepts them, ever
    /// saw them.
    /// </summary>
    [Theory]
    [InlineData("en-US", "FILE", true, false, "open the F\u0130LE now")]
    [InlineData("tr-TR", "FILE", true, false, "open the f\u0131le now")]
    [InlineData("tr-TR", "file", true, false, "open the F\u0130LE now")]
    [InlineData("tr-TR", "FIL*now", false, true, "open the f\u0131le now")]
    [InlineData("en-US", "\u1C90\u1C91\u1C92", true, false, "say \u10D0\u10D1\u10D2 now")]
    public async Task ACaseInsensitiveSearch_FindsEveryClipItsMatcherAccepts(string culture, string text, bool wholeWord, bool wildcard, string content)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try
        {
            using var scope = new TemporaryDatabaseScope();
            await scope.DatabaseInitializer.InitializeAsync();
            var clip = await scope.ClipStoreService.CaptureAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = content,
                ContentBytes = Encoding.UTF8.GetBytes(content),
                SkipPostInsertMaintenance = true,
            });

            var result = await scope.ClipStoreService.SearchAsync(
                new ClipSearchFilters { SearchText = text, WholeWord = wholeWord, UseWildcard = wildcard, Limit = 50 });

            Assert.Equal([clip!.Id], result.Items.Select(i => i.Id));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    /// <summary>
    /// The guard behind F-07-3's fix, over every BMP character with a case
    /// relationship, in the cultures whose regex casing differs. For each one the
    /// prefilter keeps, the production FTS expression must find every row whose
    /// character a case-insensitive regex accepts as equal. A newer .NET (newer
    /// Unicode casing) or a newer SQLite (newer folding table) can break that
    /// without any change here; this fails when it does.
    /// </summary>
    [Fact]
    public async Task ThePrefilterSafeCharacters_FoldLikeTheRegex()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();

        string productionFts;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'clips_fts';";
            productionFts = (string)(await command.ExecuteScalarAsync())!;
        }

        Assert.Contains("tokenize='trigram'", productionFts, StringComparison.Ordinal);

        var cultures = new[] { CultureInfo.InvariantCulture, new CultureInfo("en-US"), new CultureInfo("tr-TR"), new CultureInfo("az-Latn-AZ") };
        var universe = new SortedSet<char>();
        for (var i = 0; i < 0x10000; i++)
        {
            var c = (char)i;
            if (char.IsSurrogate(c))
            {
                continue;
            }

            foreach (var culture in cultures)
            {
                foreach (var mapped in new[] { char.ToUpper(c, culture), char.ToLower(c, culture) })
                {
                    if (mapped != c)
                    {
                        universe.Add(c);
                        universe.Add(mapped);
                    }
                }
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE VIRTUAL TABLE probe USING fts5(x, tokenize='trigram');";
            await command.ExecuteNonQueryAsync();
        }

        await using (var transaction = connection.BeginTransaction())
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO probe(rowid, x) VALUES ($r, $x);";
            var rowId = insert.Parameters.Add("$r", Microsoft.Data.Sqlite.SqliteType.Integer);
            var text = insert.Parameters.Add("$x", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var c in universe)
            {
                rowId.Value = (long)c;
                text.Value = $"q{c}z";
                await insert.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        var misses = new List<string>();
        var kept = 0;
        var saved = CultureInfo.CurrentCulture;
        try
        {
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT rowid FROM probe WHERE probe MATCH $m;";
            var match = query.Parameters.Add("$m", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var culture in cultures)
            {
                CultureInfo.CurrentCulture = culture;
                foreach (var c in universe)
                {
                    var expression = ClipStoreService.BuildPrefilterExpression(new ClipSearchFilters { SearchText = $"q{c}z", WholeWord = true });
                    if (expression is null)
                    {
                        continue;
                    }

                    kept++;
                    match.Value = expression;
                    var found = new HashSet<long>();
                    await using (var reader = await query.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            found.Add(reader.GetInt64(0));
                        }
                    }

                    var regex = new Regex("^" + Regex.Escape(c.ToString()) + "$", RegexOptions.IgnoreCase | RegexOptions.NonBacktracking | RegexOptions.Singleline);
                    misses.AddRange(universe
                        .Where(d => !found.Contains(d) && regex.IsMatch(d.ToString()))
                        .Select(d => $"'{culture.Name}' U+{(int)c:X4} -> U+{(int)d:X4}"));
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }

        Assert.True(kept > 100, $"Setup: the prefilter kept only {kept} characters, so this checked almost nothing.");
        Assert.True(misses.Count == 0, $"The prefilter keeps characters the trigram index folds differently from the regex: {string.Join(", ", misses.Take(20))}");
    }
}