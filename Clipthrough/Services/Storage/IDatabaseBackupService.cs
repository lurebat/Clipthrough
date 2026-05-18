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
}
