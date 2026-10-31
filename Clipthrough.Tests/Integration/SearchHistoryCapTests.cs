using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// F-45. Search history keeps the newest 50 queries, but the existing tests
/// saved at most three, so removing the cap survived every test.
/// </summary>
public sealed class SearchHistoryCapTests
{
    [Fact]
    public async Task SavingSixtyQueries_KeepsExactlyTheNewestFifty()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();

        for (var i = 0; i < 60; i++)
        {
            await scope.SearchHistoryService.SaveSearchAsync($"query {i:D2}");
            // used_at is the ordering key; keep the timestamps distinct.
            await Task.Delay(2);
        }

        var kept = await scope.SearchHistoryService.GetRecentSearchesAsync(100);

        Assert.Equal(Enumerable.Range(10, 50).Reverse().Select(i => $"query {i:D2}"), kept);
    }
}