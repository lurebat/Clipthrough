using System.Threading.Tasks;
using Clipthrough.Services.Search;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Services;

/// <summary>
/// Quiesces all background workers and clears the SQLite connection pool
/// before a whole-database operation (rekey, path move, backup, restore),
/// then restores everything on disposal — even when the operation body throws.
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

    private DatabaseMaintenanceScope(
        IClipboardMonitorService? monitor,
        IBackgroundOcrQueue? ocrQueue,
        IEmbeddingWorker? embeddingWorker)
    {
        _monitor = monitor;
        _ocrQueue = ocrQueue;
        _embeddingWorker = embeddingWorker;
    }

    /// <summary>
    /// Stops <paramref name="monitor"/>, <paramref name="ocrQueue"/>, and
    /// <paramref name="embeddingWorker"/> (in that order, waiting for each to
    /// drain), then calls <see cref="SqliteConnection.ClearAllPools()"/> so no
    /// pooled connection holds the DB file open.
    /// Returns a scope whose <see cref="DisposeAsync"/> reverses the steps:
    /// clear pools again, then restart each worker.
    /// </summary>
    public static async Task<DatabaseMaintenanceScope> EnterAsync(
        IClipboardMonitorService? monitor,
        IBackgroundOcrQueue? ocrQueue,
        IEmbeddingWorker? embeddingWorker)
    {
        var scope = new DatabaseMaintenanceScope(monitor, ocrQueue, embeddingWorker);

        try
        {
            monitor?.Stop();

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
    /// connections), then restarts each worker.
    /// </summary>
    public System.Threading.Tasks.ValueTask DisposeAsync()
    {
        // Clear again so connections opened during the operation body don't
        // linger and reopen the now-replaced/moved database file.
        SqliteConnection.ClearAllPools();

        _monitor?.Start();
        _ocrQueue?.Start();
        _embeddingWorker?.Start();

        return System.Threading.Tasks.ValueTask.CompletedTask;
    }
}
