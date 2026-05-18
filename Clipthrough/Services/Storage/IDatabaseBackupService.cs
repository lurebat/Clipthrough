using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Clipthrough.Services;

public interface IDatabaseBackupService
{
    /// <summary>
    /// Ensures a backup exists for today's UTC date. No-op if one already
    /// exists; otherwise copies the (checkpointed) database file into a
    /// <c>backups/</c> sub-folder and prunes older backups beyond the
    /// retention limit. Safe to call multiple times per launch.
    /// </summary>
    Task EnsureDailyBackupAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the list of available backup snapshots, newest first.
    /// </summary>
    IReadOnlyList<DatabaseBackupInfo> ListBackups();

    /// <summary>
    /// Restores the given backup over the live database. The caller is
    /// responsible for ensuring no other connection is open (i.e. background
    /// services stopped, current connection-using operations finished). The
    /// live <c>.db</c>, <c>.db-wal</c>, and <c>.db-shm</c> files are renamed to
    /// <c>.before-restore-{timestamp}</c> instead of being deleted, so the
    /// pre-restore state remains recoverable.
    /// </summary>
    Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default);
}

public sealed record DatabaseBackupInfo(string Path, System.DateTimeOffset Timestamp, long Size);

