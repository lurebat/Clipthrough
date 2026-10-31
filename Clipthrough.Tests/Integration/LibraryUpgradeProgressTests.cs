using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// U-E1, owner decision (C): what <c>InitializeAsync</c> tells the loading overlay
/// about the first start after an upgrade. The headless test covers the overlay;
/// these pin the reports themselves.
/// </summary>
public sealed class LibraryUpgradeProgressTests
{
    /// <summary>Records on the reporting thread, unlike <see cref="Progress{T}"/>, which posts.</summary>
    private sealed class Recorder : IProgress<LibraryUpgradeProgress>
    {
        public List<LibraryUpgradeProgress> Reports { get; } = [];

        public void Report(LibraryUpgradeProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }

    [Fact]
    public async Task ANewLibrary_ReportsNoUpgrade()
    {
        using var scope = new TemporaryDatabaseScope();
        var recorder = new Recorder();

        await scope.DatabaseInitializer.InitializeAsync(recorder);

        Assert.Empty(recorder.Reports);
    }

    [Fact]
    public async Task AnUpgradedLibrary_ReportsItsStagesInOrder_AndTheMoveRunsToCompletion()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        for (var i = 0; i < 5; i++)
        {
            var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
            {
                ContentType = ContentType.Text,
                ContentFormat = ClipContentFormat.PlainText,
                ContentText = $"clip {i}",
                ContentBytes = Encoding.UTF8.GetBytes($"clip {i}"),
            });
            Assert.NotNull(clip);
        }

        await using (var connection = scope.ConnectionFactory.CreateConnection())
        {
            await connection.OpenAsync();
            await using var rewind = connection.CreateCommand();
            rewind.CommandText = "UPDATE app_metadata SET value = CAST(CAST(value AS INTEGER) - 1 AS TEXT) WHERE key = 'schema_version';";
            await rewind.ExecuteNonQueryAsync();
        }

        var recorder = new Recorder();
        await scope.DatabaseInitializer.InitializeAsync(recorder);

        var stages = recorder.Reports.Select(r => r.Stage).Distinct().ToArray();
        Assert.Equal(
            [LibraryUpgradeStage.CheckingIntegrity, LibraryUpgradeStage.UpdatingStructure, LibraryUpgradeStage.MovingClipData, LibraryUpgradeStage.Finishing],
            stages);

        var moved = recorder.Reports.Where(r => r.Stage == LibraryUpgradeStage.MovingClipData).Select(r => r.Fraction).ToArray();
        Assert.All(moved, f => Assert.InRange(f!.Value, 0, 1));
        Assert.Equal(moved.OrderBy(f => f), moved);
        Assert.Equal(1, moved[^1]);
    }
}
