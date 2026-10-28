using System;
using System.Linq;
using System.Threading.Tasks;
using Clipthrough.Database;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>Runs alone, so no other test's allocations land inside a measured window.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationBudgetCollection
{
    public const string Name = "Allocation budget";
}

/// <summary>
/// U-E1, owner decision (c): the payload move is SQL-only. Payload bytes are
/// copied inside SQLite and never cross into managed memory, which is what made
/// the first version take 87 s on a 1 GB library.
/// </summary>
[Collection(AllocationBudgetCollection.Name)]
public sealed class PayloadMoveAllocationTests
{
    private const int Rows = 16;
    private const int PayloadBytes = 4 * 1024 * 1024;

    [Fact]
    public async Task MovingAnExistingLibrary_DoesNotPullThePayloadsIntoManagedMemory()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        var icon = Enumerable.Range(0, 2048).Select(static i => (byte)(i * 7)).ToArray();

        await using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            for (var i = 0; i < Rows; i++)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO clips (content, content_bytes, content_type, content_format, source_app, source_app_icon,
                        hash, captured_at, first_copied_at, last_copied_at, byte_size, sensitivity_scanned_at)
                    VALUES ($c, zeroblob($n), 'image', 'bitmap', 'Paint', $icon, $h, $t, $t, $t, $n, $t);
                    """;
                command.Parameters.AddWithValue("$c", $"Image {i}");
                command.Parameters.AddWithValue("$n", PayloadBytes);
                command.Parameters.AddWithValue("$icon", icon);
                command.Parameters.AddWithValue("$h", $"legacy-{i}");
                command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.AddMinutes(i).ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            await using var rewind = connection.CreateCommand();
            rewind.CommandText = "UPDATE app_metadata SET value = '8' WHERE key = 'schema_version'; DELETE FROM app_metadata WHERE key = 'payload_move_last_id';";
            await rewind.ExecuteNonQueryAsync();
        }

        var before = GC.GetTotalAllocatedBytes(precise: true);
        await scope.DatabaseInitializer.InitializeAsync();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        await using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM clip_payloads WHERE length(content_bytes) = $n;";
            check.Parameters.AddWithValue("$n", PayloadBytes);
            Assert.Equal(Rows, Convert.ToInt32(await check.ExecuteScalarAsync()));
        }

        // The payloads total 64 MB, so reading them through .NET even once would
        // allocate at least that. The budget leaves room for everything else
        // InitializeAsync allocates and is still a quarter of one copy.
        const long budget = 16L * 1024 * 1024;
        Assert.True(
            allocated < budget,
            $"InitializeAsync allocated {allocated / (1024 * 1024)} MB moving {Rows} payloads of {PayloadBytes / (1024 * 1024)} MB; the budget is {budget / (1024 * 1024)} MB.");
    }
}
