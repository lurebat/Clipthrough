using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipthrough.Models;
using Xunit;

namespace Clipthrough.Tests.Integration;

/// <summary>
/// Repairing the FTS schema has to repopulate it. Dropping the table and letting
/// the schema DDL recreate it leaves an empty external-content index, and every
/// clip already in the database stops being findable by keyword until something
/// else happens to trigger a rebuild.
/// </summary>
/// <remarks>
/// The repair ran before the schema-version gate while the rebuild sat inside
/// it, so the two only agreed when the version was also behind. That the
/// combination is reachable is not an inference: HasPendingStructuralWorkAsync
/// ends with a bare FtsSchemaNeedsMigrationAsync check, reached only when the
/// version is already current, and pays for a full-file PRAGMA quick_check on
/// the strength of it. One half of the initializer treated the case as real
/// while the other assumed it away. (round 2, arch-sol A16)
/// </remarks>
public sealed class FtsSchemaRepairTests
{
    private static async Task CaptureAsync(TemporaryDatabaseScope scope, string text)
    {
        var clip = await scope.ClipStoreService.CaptureFastAsync(new ClipCaptureRequest
        {
            ContentType = ContentType.Text,
            ContentFormat = ClipContentFormat.PlainText,
            ContentText = text,
            ContentBytes = Encoding.UTF8.GetBytes(text),
        });

        Assert.NotNull(clip);
    }

    private static async Task<string[]> SearchAsync(TemporaryDatabaseScope scope, string query)
    {
        var result = await scope.ClipStoreService.SearchAsync(new ClipSearchFilters { SearchText = query });
        return result.Items.Select(i => i.Content).OrderBy(c => c, StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Leaves the schema version current - the whole point - while putting back
    /// the single-column unicode61 index an old build left behind.
    /// </summary>
    private static void MakeFtsSchemaLegacy(TemporaryDatabaseScope scope)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DROP TRIGGER IF EXISTS clips_ai;" +
            "DROP TRIGGER IF EXISTS clips_ad;" +
            "DROP TRIGGER IF EXISTS clips_au;" +
            "DROP TABLE IF EXISTS clips_fts;" +
            "CREATE VIRTUAL TABLE clips_fts USING fts5(content, tokenize='unicode61');";
        command.ExecuteNonQuery();
    }

    private static long StoredSchemaVersion(TemporaryDatabaseScope scope)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_metadata WHERE key = 'schema_version';";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Counts rows the FTS index itself can return for a term.
    /// </summary>
    /// <remarks>
    /// Deliberately a MATCH and not COUNT(*). clips_fts is an external-content
    /// table (content='clips'), so a bare COUNT(*) is answered from the clips
    /// table and reports the same number whether the index holds every row or
    /// nothing at all. The first version of this test did exactly that and
    /// passed against the unfixed code.
    /// </remarks>
    private static long FtsMatchCount(TemporaryDatabaseScope scope, string term)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM clips_fts WHERE clips_fts MATCH $term;";
        command.Parameters.AddWithValue("$term", term);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task RepairingTheFtsSchemaAtTheCurrentVersion_StillFindsClipsStoredBeforeIt()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await CaptureAsync(scope, "quarterly revenue projection");

        var versionBefore = StoredSchemaVersion(scope);
        MakeFtsSchemaLegacy(scope);
        await scope.DatabaseInitializer.InitializeAsync();

        // The fixture is only meaningful while the version never moved: if the
        // repair path stamped it backwards, the ordinary migration would be
        // doing the rebuild and this would prove nothing about the repair.
        Assert.Equal(versionBefore, StoredSchemaVersion(scope));

        var hits = await SearchAsync(scope, "revenue");

        Assert.Equal(["quarterly revenue projection"], hits);
    }

    /// <summary>
    /// Asserts the index itself, not just the search. The search path can fall
    /// back to a substring scan, so a passing search does not prove the index
    /// was repopulated - and a later change that made every query take the
    /// fallback would leave the test above green over an empty index.
    /// </summary>
    [Fact]
    public async Task RepairingTheFtsSchemaAtTheCurrentVersion_RepopulatesTheIndexItself()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await CaptureAsync(scope, "quarterly revenue projection");
        await CaptureAsync(scope, "annual revenue headcount plan");

        MakeFtsSchemaLegacy(scope);
        Assert.Equal(0, FtsMatchCount(scope, "revenue"));

        await scope.DatabaseInitializer.InitializeAsync();

        Assert.Equal(2, FtsMatchCount(scope, "revenue"));
    }

    /// <summary>
    /// The control: a database whose FTS schema is already current must not be
    /// rebuilt. A repair that always ran would satisfy both tests above while
    /// making every launch of a large library pay for a full index rebuild.
    /// </summary>
    /// <remarks>
    /// Asserts on the traced step rather than on the resulting index, because
    /// the result of an unnecessary rebuild is byte-identical to not rebuilding
    /// - same definition, same rows, same hits. The first version of this test
    /// compared the schema SQL and the match count and passed happily against a
    /// repair forced to run every time, which is the exact regression it exists
    /// to catch.
    /// </remarks>
    [Fact]
    public async Task AnUpToDateFtsSchema_IsNotRebuiltOnTheNextLaunch()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await CaptureAsync(scope, "quarterly revenue projection");

        var steps = await CaptureInitializationStepsAsync(scope);

        Assert.DoesNotContain(steps, step => step.Contains("rebuild-search-index", StringComparison.Ordinal));
        Assert.Contains(steps, step => step.Contains("schema-up-to-date", StringComparison.Ordinal));
        Assert.Equal(1, FtsMatchCount(scope, "revenue"));
        Assert.Equal(["quarterly revenue projection"], await SearchAsync(scope, "revenue"));
    }

    /// <summary>
    /// The other side of the control: the same trace must show the rebuild when
    /// the schema really was repaired. Without it "never rebuild" would satisfy
    /// the assertion above.
    /// </summary>
    [Fact]
    public async Task ARepairedFtsSchema_TracesTheRebuildItPerformed()
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await CaptureAsync(scope, "quarterly revenue projection");
        MakeFtsSchemaLegacy(scope);

        var steps = await CaptureInitializationStepsAsync(scope);

        Assert.Contains(steps, step => step.Contains("rebuild-search-index", StringComparison.Ordinal));
    }

    /// <summary>
    /// A launch killed after the repair dropped the old table, but before the
    /// rebuild committed, must be finished by the next launch. By then the
    /// recreated table already has the current definition, so the repair check
    /// no longer fires; on a library whose version is behind but past
    /// LastSearchIndexChangeVersion the version gate does not rebuild either, and
    /// at the current version nothing did. Either way every stored clip silently
    /// dropped out of keyword search for good. (Rubber-duck F-11-2)
    /// </summary>
    /// <remarks>
    /// The rebuild is one autocommit statement, so a kill part-way through it
    /// rolls back to the same empty table as a kill just before it. Throwing from
    /// the RebuildingSearchIndex progress report, which comes right before the
    /// statement, reproduces that state deterministically.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARepairInterruptedBeforeTheRebuild_IsFinishedByTheNextLaunch(bool versionBehind)
    {
        using var scope = new TemporaryDatabaseScope();
        await scope.DatabaseInitializer.InitializeAsync();
        await CaptureAsync(scope, "quarterly revenue projection");
        await CaptureAsync(scope, "annual revenue headcount plan");

        var current = StoredSchemaVersion(scope);
        MakeFtsSchemaLegacy(scope);
        if (versionBehind)
        {
            SetStoredSchemaVersion(scope, current - 1);
        }

        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            scope.DatabaseInitializer.InitializeAsync(new CrashAtStage(LibraryUpgradeStage.RebuildingSearchIndex)));

        // The state the kill left: the table recreated with the current
        // definition, and empty.
        Assert.Equal(0, FtsMatchCount(scope, "revenue"));

        var steps = await CaptureInitializationStepsAsync(scope);

        Assert.Equal(2, FtsMatchCount(scope, "revenue"));
        Assert.Equal(current, StoredSchemaVersion(scope));
        // The rebuild rewrites pages, so it buys the integrity check like any
        // other structural work.
        Assert.Contains(steps, step => step.Contains("integrity-check", StringComparison.Ordinal));

        // Finished means finished: the launch after that is an ordinary one.
        var nextSteps = await CaptureInitializationStepsAsync(scope);
        Assert.DoesNotContain(nextSteps, step => step.Contains("rebuild-search-index", StringComparison.Ordinal));
        Assert.Contains(nextSteps, step => step.Contains("schema-up-to-date", StringComparison.Ordinal));
    }

    private static void SetStoredSchemaVersion(TemporaryDatabaseScope scope, long version)
    {
        using var connection = scope.ConnectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE app_metadata SET value = $version WHERE key = 'schema_version';";
        command.Parameters.AddWithValue("$version", version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private sealed class SimulatedCrashException : Exception;

    private sealed class CrashAtStage(LibraryUpgradeStage stage) : IProgress<LibraryUpgradeProgress>
    {
        public void Report(LibraryUpgradeProgress value)
        {
            if (value.Stage == stage)
            {
                throw new SimulatedCrashException();
            }
        }
    }

    private static async Task<string[]> CaptureInitializationStepsAsync(TemporaryDatabaseScope scope)
    {
        var sink = new ConcurrentQueue<string>();
        var listener = new TraceCaptureListener(sink);
        Trace.Listeners.Add(listener);
        try
        {
            await scope.DatabaseInitializer.InitializeAsync();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
        }

        var steps = sink.Where(message => message.Contains("[init-timing]", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(steps);
        return steps;
    }

    private sealed class TraceCaptureListener(ConcurrentQueue<string> sink) : TraceListener
    {
        public override void Write(string? message)
        {
            if (message is not null)
            {
                sink.Enqueue(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                sink.Enqueue(message);
            }
        }
    }
}
