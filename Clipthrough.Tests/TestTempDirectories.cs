using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Tests;

/// <summary>
/// Owns <c>%TEMP%\Clipthrough.Tests</c>, the root every per-test database
/// directory lives under.
///
/// Deleting one of those directories used to fail silently: Microsoft.Data.Sqlite
/// pools connections, a disposed connection keeps its file handle open in the
/// pool, and the resulting IOException was swallowed. Every run left one
/// directory per test behind, which grew to tens of gigabytes on a dev machine.
/// See docs/solutions/test-temp-databases-leak-through-the-connection-pool.md.
/// </summary>
internal static partial class TestTempDirectories
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "Clipthrough.Tests");

    /// <summary>
    /// Old enough that no live run can still own the directory. A test run is
    /// minutes; a day leaves a wide margin for one that is paused in a debugger.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private const int DeleteAttempts = 5;

    public static string CreateDirectory()
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Releases pooled SQLite handles, then deletes <paramref name="path"/>,
    /// retrying briefly for a scanner or indexer that has the file open. Best
    /// effort: a teardown failure must not fail the test that owned it, and the
    /// stale sweep collects anything left behind.
    /// </summary>
    public static bool TryDelete(string path)
    {
        SqliteConnection.ClearAllPools();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == DeleteAttempts)
                {
                    return false;
                }

                Thread.Sleep(50 * attempt);
            }
        }
    }

    /// <summary>
    /// Deletes per-test directories under <paramref name="root"/> that are older
    /// than <paramref name="staleAfter"/>. Only GUID-named directories directly
    /// under the root are touched, so nothing this class did not create is at risk.
    /// </summary>
    public static int SweepStale(string root, TimeSpan staleAfter)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow - staleAfter;
        var removed = 0;
        foreach (var path in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (!PerTestDirectoryName().IsMatch(Path.GetFileName(path))
                    || Directory.GetCreationTimeUtc(path) >= cutoff
                    || Directory.GetLastWriteTimeUtc(path) >= cutoff)
                {
                    continue;
                }

                Directory.Delete(path, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    /// <summary>
    /// Clears what earlier runs leaked, without making this run wait for it: the
    /// first sweep after a long leak can be tens of thousands of directories. If
    /// the run ends first, the next one carries on.
    /// </summary>
    [ModuleInitializer]
    internal static void SweepStaleInBackground()
    {
        var thread = new Thread(() =>
        {
            try
            {
                SweepStale(Root, StaleAfter);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Clipthrough.Tests stale temp sweep",
        };
        thread.Start();
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex PerTestDirectoryName();
}
