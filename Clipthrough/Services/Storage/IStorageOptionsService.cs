using System.Threading;
using System.Threading.Tasks;
using Clipthrough.Models;

namespace Clipthrough.Services;

public interface IStorageOptionsService
{
    StorageOptions Current { get; }

    bool HasSavedConfig { get; }

    bool DatabaseExists { get; }

    Task SaveAsync(StorageOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the database password in memory only, without persisting or triggering rekey.
    /// Used after validating the password at unlock time.
    /// </summary>
    void SetInMemoryPassword(string password);

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
}