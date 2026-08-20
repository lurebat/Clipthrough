using System.Threading.Tasks;
using Clipthrough.Services.Search;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Services;

/// <summary>
/// Quiesces all background workers and clears the SQLite connection pool
/// before a whole-database operation (rekey, path move, backup, restore),
/// then restores them on disposal — even when the operation body throws.
///
/// Only workers that were running on entry are restarted. A worker the user
/// deliberately turned off (capture paused, semantic search disabled) must not
/// come back to life just because a maintenance operation ran.
///
/// Usage:
/// <code>
/// await using var scope = await DatabaseMaintenanceScope.EnterAsync(monitor, ocrQueue, embeddingWorker);
/// // … whole-DB operation …
/// </code>
///
/// When any of the worker references are null (e.g. in test contexts) the
/// corresponding stop/start calls are skipped; the pool clear always runs.
/// </summary>
public sealed class DatabaseMaintenanceScope : System.IAsyncDisposable
{
    private readonly IClipboardMonitorService? _monitor;
    private readonly IBackgroundOcrQueue? _ocrQueue;
    private readonly IEmbeddingWorker? _embeddingWorker;
    private readonly bool _monitorWasRunning;
    private readonly bool _ocrQueueWasRunning;
    private readonly bool _embeddingWorkerWasRunning;

    private DatabaseMaintenanceScope(
        IClipboardMonitorService? monitor,
        IBackgroundOcrQueue? ocrQueue,
        IEmbeddingWorker? embeddingWorker)
    {
        _monitor = monitor;
        _ocrQueue = ocrQueue;
        _embeddingWorker = embeddingWorker;

        // Snapshot before anything is stopped: disposal restores this state rather
        // than starting everything, so a worker that was deliberately off (capture
        // paused, semantic search disabled, the model file missing) stays off.
        _monitorWasRunning = monitor?.IsRunning ?? false;
        _ocrQueueWasRunning = ocrQueue?.IsRunning ?? false;
        _embeddingWorkerWasRunning = embeddingWorker?.IsRunning ?? false;
    }

    /// <summary>
    /// Stops <paramref name="monitor"/>, <paramref name="ocrQueue"/>, and
    /// <paramref name="embeddingWorker"/> (in that order, waiting for each to
    /// drain), then calls <see cref="SqliteConnection.ClearAllPools()"/> so no
    /// pooled connection holds the DB file open.
    /// Returns a scope whose <see cref="DisposeAsync"/> reverses the steps:
    /// clear pools again, then restart each worker.
    /// </summary>
    /// <remarks>
    /// "Waiting for each to drain" was untrue of the monitor for as long as this
    /// comment existed: it was stopped through the void <c>Stop()</c>, which off
    /// the UI thread only posts. The monitor now has a <c>StopAsync</c> that
    /// waits, and the sentence is true of all three.
    /// </remarks>
    public static async Task<DatabaseMaintenanceScope> EnterAsync(
        IClipboardMonitorService? monitor,
        IBackgroundOcrQueue? ocrQueue,
        IEmbeddingWorker? embeddingWorker)
    {
        var scope = new DatabaseMaintenanceScope(monitor, ocrQueue, embeddingWorker);

        try
        {
            if (monitor is not null)
            {
                // StopAsync, not Stop: the pool clear below and whatever the
                // caller does next (move, rekey, replace the file) must not
                // overlap a clipboard-originated write. Stop only posts when
                // called off the UI thread, and never waits for the enrichment
                // that outlives a capture. (arch-sol A6)
                await monitor.StopAsync().ConfigureAwait(false);
            }

            if (ocrQueue is not null)
            {
                await ocrQueue.StopAsync().ConfigureAwait(false);
            }

            if (embeddingWorker is not null)
            {
                await embeddingWorker.StopAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // A stop failed partway through; restart whatever was already stopped
            // (Start is idempotent) so we never leave the workers permanently down,
            // then surface the failure. Without this the caller never receives the
            // scope, so its DisposeAsync — the only restart path — never runs.
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // Release any pooled connections so the database file is not
        // kept open by a dangling connection handle.
        SqliteConnection.ClearAllPools();

        return scope;
    }

    /// <summary>
    /// Clears the connection pool a second time (in case the body opened new
    /// connections), then restarts each worker that was running on entry.
    /// </summary>
    public System.Threading.Tasks.ValueTask DisposeAsync()
    {
        // Clear again so connections opened during the operation body don't
        // linger and reopen the now-replaced/moved database file.
        SqliteConnection.ClearAllPools();

        if (_monitorWasRunning)
        {
            _monitor?.Start();
        }

        if (_ocrQueueWasRunning)
        {
            _ocrQueue?.Start();
        }

        if (_embeddingWorkerWasRunning)
        {
            _embeddingWorker?.Start();
        }

        return System.Threading.Tasks.ValueTask.CompletedTask;
    }
}
