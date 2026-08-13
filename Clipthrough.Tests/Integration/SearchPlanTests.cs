using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;
using Clipthrough.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Search is tokenised with trigrams and fires from the third character typed, so a
/// common substring matches most of the library. Sorting every match means fetching
/// every matching row, and clips carry their content and source-app icon inline -
/// measured at 60k clips, a term matching everything took 940 ms. Walking the sort's
/// own index and testing membership answers the same page in 63 ms without touching a
/// row it will not return. The reverse holds for a term matching almost nothing
/// (0.7 ms against 20 ms), so the service picks per query.
///
/// These tests defend three things: that the choice is made on the match count, that
/// each shape really gets the plan it was chosen for, and that both return the same
/// rows.
/// </summary>
public class SearchPlanTests
{
    private const string CommonToken = "invoice";
    private const string RareToken = "zzqqxx";

    /// <summary>
    /// Seeds enough clips that a search for <see cref="CommonToken"/> is over the
    /// broad-search threshold and a search for <see cref="RareToken"/> is far under it.
    /// </summary>
    private static async Task SeedAsync(TemporaryDatabaseScope scope, int commonRows, int rareRows)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO clips (content, content_type, content_format, source_app, hash,
                is_favorite, is_sensitive, is_pasted, copy_count, paste_count,
                first_copied_at, last_copied_at, captured_at, byte_size)
            VALUES ($c, 0, 0, 'app.exe', $h, $fav, 0, 0, 1, $paste, $t, $t, $t, $b);
            """;
        var pc = command.Parameters.Add("$c", SqliteType.Text);
        var ph = command.Parameters.Add("$h", SqliteType.Text);
        var pfav = command.Parameters.Add("$fav", SqliteType.Integer);
        var ppaste = command.Parameters.Add("$paste", SqliteType.Integer);
        var pt = command.Parameters.Add("$t", SqliteType.Text);
        var pb = command.Parameters.Add("$b", SqliteType.Integer);
        var baseTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var total = commonRows + rareRows;
        for (var i = 0; i < total; i++)
        {
            var content = i < commonRows ? $"paid {CommonToken} number {i}" : $"{RareToken} outlier {i}";
            pc.Value = content;
            ph.Value = $"hash{i}";
            pfav.Value = i % 7 == 0 ? 1 : 0;
            ppaste.Value = i % 5;
            pt.Value = baseTime.AddSeconds(i).ToString("O");
            pb.Value = content.Length;
            await command.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private static async Task<IReadOnlyList<string>> PlanAsync(TemporaryDatabaseScope scope, ClipSearchFilters filters, bool useOrderedIndexPlan)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // EXPLAIN the query the service itself composes, not a copy of it.
        command.CommandText = "EXPLAIN QUERY PLAN " + ClipStoreService.BuildSearchSql(filters, hasSearch: true, useOrderedIndexPlan);
        command.Parameters.AddWithValue("$search", $"\"{filters.SearchText}\"");
        command.Parameters.AddWithValue("$limit", filters.Limit + 1);
        command.Parameters.AddWithValue("$offset", filters.Offset);

        var steps = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) steps.Add(reader.GetString(3));
        return steps;
    }

    private static async Task<bool> IsBroadAsync(TemporaryDatabaseScope scope, string term)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        return await ClipStoreService.IsBroadSearchAsync(
            connection,
            new ClipSearchFilters { SearchText = term },
            CancellationToken.None);
    }

    [Fact]
    public async Task BroadSearch_WalksTheOrderedIndexInsteadOfSortingEveryMatch()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedAsync(scope, commonRows: 2500, rareRows: 10);

        Assert.True(await IsBroadAsync(scope, CommonToken), "a term matching 2500 clips should count as broad");

        var filters = new ClipSearchFilters { SearchText = CommonToken, Limit = 50 };
        var plan = await PlanAsync(scope, filters, useOrderedIndexPlan: true);

        // The whole point: no sort step, because the index already supplies the order.
        Assert.DoesNotContain(plan, s => s.Contains("USE TEMP B-TREE FOR ORDER BY", StringComparison.Ordinal));
        Assert.Contains(plan, s => s.Contains("idx_clips_default_order", StringComparison.Ordinal));

        // ...and the slow shape it replaces really does sort, so the assertion above
        // discriminates rather than passing against either implementation.
        var slowPlan = await PlanAsync(scope, filters, useOrderedIndexPlan: false);
        Assert.Contains(slowPlan, s => s.Contains("USE TEMP B-TREE FOR ORDER BY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NarrowSearch_IsNotTreatedAsBroad()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedAsync(scope, commonRows: 2500, rareRows: 10);

        // Walking the whole index to find ten rows is slower than sorting ten rows,
        // so a rare term must keep the FTS-driven shape.
        Assert.False(await IsBroadAsync(scope, RareToken));
    }

    [Fact]
    public async Task BroadSearch_ReturnsTheSameRowsAsTheSortedShape()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedAsync(scope, commonRows: 2500, rareRows: 10);

        foreach (var sort in new[] { ClipSortOption.MostRecent, ClipSortOption.OldestFirst, ClipSortOption.MostPasted, ClipSortOption.LargestFirst })
        {
            foreach (var offset in new[] { 0, 137 })
            {
                var filters = new ClipSearchFilters { SearchText = CommonToken, Limit = 25, Offset = offset, SortOption = sort };
                var fast = await RunAsync(scope, filters, useOrderedIndexPlan: true);
                var slow = await RunAsync(scope, filters, useOrderedIndexPlan: false);

                Assert.Equal(25, fast.Count);
                Assert.Equal(slow, fast);
            }
        }
    }

    [Fact]
    public async Task BroadSearch_HonoursStructuralFiltersUnchanged()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedAsync(scope, commonRows: 2500, rareRows: 10);

        var filters = new ClipSearchFilters { SearchText = CommonToken, Limit = 30, FavoritesOnly = true };
        var fast = await RunAsync(scope, filters, useOrderedIndexPlan: true);
        var slow = await RunAsync(scope, filters, useOrderedIndexPlan: false);

        Assert.NotEmpty(fast);
        Assert.Equal(slow, fast);
    }

    /// <summary>
    /// INDEXED BY makes SQLite refuse the query outright if the named index is missing,
    /// so a rename or a dropped index would break search rather than merely slow it.
    /// </summary>
    [Fact]
    public async Task EverySortWithAForcedIndex_HasThatIndexAndRuns()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedAsync(scope, commonRows: 100, rareRows: 0);

        var checkedAny = false;
        foreach (var sort in Enum.GetValues<ClipSortOption>())
        {
            var index = ClipStoreService.OrderCoveringIndex(sort);
            if (index is null) continue;
            checkedAny = true;

            var filters = new ClipSearchFilters { SearchText = CommonToken, Limit = 5, SortOption = sort };
            // Throws SqliteException "no query solution" if the index does not exist.
            var rows = await RunAsync(scope, filters, useOrderedIndexPlan: true);
            Assert.NotEmpty(rows);
        }

        Assert.True(checkedAny, "no sort declares a covering index; the fast plan can never be used");
    }

    private static async Task<List<long>> RunAsync(TemporaryDatabaseScope scope, ClipSearchFilters filters, bool useOrderedIndexPlan)
    {
        await using var connection = scope.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = ClipStoreService.BuildSearchSql(filters, hasSearch: true, useOrderedIndexPlan);
        command.Parameters.AddWithValue("$search", $"\"{filters.SearchText}\"");
        command.Parameters.AddWithValue("$limit", filters.Limit);
        command.Parameters.AddWithValue("$offset", filters.Offset);

        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    /// <summary>
    /// The end-to-end contract: a broad search through the real service still returns
    /// a correct, correctly ordered page.
    /// </summary>
    [Fact]
    public async Task SearchAsync_BroadTerm_ReturnsNewestFirstAcrossPages()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await SeedAsync(scope, commonRows: 2500, rareRows: 10);

        var page0 = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = CommonToken, Limit = 50 });
        var page1 = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = CommonToken, Limit = 50, Offset = 50 });

        Assert.Equal(50, page0.Items.Count);
        Assert.Equal(50, page1.Items.Count);

        // Newest first, and the two pages must not overlap.
        var ids = page0.Items.Concat(page1.Items).Select(i => i.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(ids.OrderByDescending(i => i).ToList(), ids);

        // The rare rows were captured last, so they lead the library but must not
        // appear in a search that does not match them.
        Assert.All(page0.Items, i => Assert.Contains(CommonToken, i.Content, StringComparison.Ordinal));
    }
}
