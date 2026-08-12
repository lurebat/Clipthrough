using Clipthrough.Localization;
using Microsoft.Data.Sqlite;

namespace Clipthrough.Database;

/// <summary>
/// Turns a <see cref="SqliteException"/> into something a user can act on.
/// </summary>
public static class SqliteErrors
{
    /// <summary>
    /// SQLITE_NOTADB. With SQLCipher this is what a wrong key looks like: the
    /// header decrypts to nonsense, which is indistinguishable from a file that
    /// genuinely is not a database. Measured against this app's provider
    /// (SQLitePCLRaw.bundle_e_sqlcipher): a wrong password, no password at all
    /// against an encrypted database, and a text file all report 26, while a
    /// missing file reports 14 (SQLITE_CANTOPEN).
    /// </summary>
    private const int NotADatabase = 26;

    /// <summary>
    /// Whether a failed open is consistent with the password being wrong.
    ///
    /// Only error 26 is. Everything else - a missing or moved file, a disk I/O
    /// error, a lock held past the busy timeout - has nothing to do with the
    /// password, and telling the user their password is wrong sends them off to
    /// retype a password that was right all along.
    /// </summary>
    public static bool IsPasswordFailure(SqliteException exception)
    {
        System.ArgumentNullException.ThrowIfNull(exception);
        return exception.SqliteErrorCode == NotADatabase;
    }

    /// <summary>
    /// The message to show when unlocking the database failed.
    /// </summary>
    public static string DescribeUnlockFailure(SqliteException exception)
        => IsPasswordFailure(exception)
            ? AppText.UnlockIncorrectPassword
            : AppText.UnlockDatabaseUnreadable;
}
