using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface IStorageOptionsService
{
    StorageOptions Current { get; }

    /// <summary>
    /// The data folder this session runs from. A relative or missing library
    /// path is resolved against it, never against the working directory.
    /// </summary>
    AppDataRoot DataRoot { get; }

    bool HasSavedConfig { get; }

    /// <summary>
    /// Describes an unreadable storage.json found at load, or null when the
    /// storage settings came from where they were supposed to. Set once, at
    /// construction. When set, <see cref="Current"/> is the default library,
    /// and the user has to be told so.
    /// </summary>
    string? LoadFault { get; }

    bool DatabaseExists { get; }

    Task SaveAsync(StorageOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the database password in memory only, without persisting or triggering rekey.
    /// Used after validating the password at unlock time.
    /// </summary>
    void SetInMemoryPassword(string password);

    /// <summary>
    /// Opens the configured database with <paramref name="password"/> on a
    /// background thread and returns the failure, or <c>null</c> when it opened.
    ///
    /// Off the calling thread because SQLCipher derives the key on open, which
    /// measures ~530ms and is unaffected by the size of the database. Awaiting
    /// SqliteConnection.OpenAsync does not avoid this: it derives the key on the
    /// calling thread and returns an already-completed task, so calling it from
    /// the UI thread freezes the window for half a second.
    ///
    /// Returns the exception rather than a bool because a wrong password, a moved
    /// file and a disk error all fail here and only the first should be reported
    /// as a wrong password.
    /// </summary>
    Task<Microsoft.Data.Sqlite.SqliteException?> TryOpenWithPasswordAsync(string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-encrypts the database file in place. Opens the database with
    /// <paramref name="currentPassword"/>, runs the rekey pragma to set the
    /// new key, and updates <see cref="Current"/> with the new password.
    /// The new password is persisted to disk only when
    /// <paramref name="rememberNewPassword"/> is <c>true</c>.
    /// Throws <see cref="System.InvalidOperationException"/> when
    /// <paramref name="currentPassword"/> does not unlock the database.
    /// </summary>
    Task RekeyAsync(string currentPassword, string newPassword, bool rememberNewPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="action"/> with the options current at that moment,
    /// while no move, rekey or other storage save is in progress. For work that
    /// has to agree with the live database's path and key, such as publishing
    /// a backup copy taken without holding that lock.
    /// </summary>
    Task RunExclusiveAsync(System.Action<StorageOptions> action, CancellationToken cancellationToken = default);
}