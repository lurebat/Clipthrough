using System;
using System.IO;

namespace Clipthrough.Models;

public sealed record StorageOptions
{
    /// <summary>
    /// Empty means "the default library for the data root";
    /// <see cref="Normalize"/> fills it in.
    /// </summary>
    public string DatabasePath { get; init; } = string.Empty;

    public string DatabasePassword { get; init; } = string.Empty;

    /// <summary>
    /// When true, the password is persisted to storage.json so the database
    /// auto-unlocks on next launch. When false, the password lives only in
    /// memory for the current session.
    /// </summary>
    public bool RememberPassword { get; init; }

    public static StorageOptions Default { get; } = new();

    public StorageOptions Normalize(AppDataRoot dataRoot)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(DatabasePath)
            ? dataRoot.DefaultDatabasePath
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(DatabasePath.Trim()), dataRoot.Directory);

        return this with
        {
            DatabasePath = normalizedPath,
            DatabasePassword = NormalizePassword(DatabasePassword),
            RememberPassword = RememberPassword,
        };
    }

    /// <summary>
    /// The one rule for turning what a user typed into a database key. Unlock,
    /// settings and rekey all go through it, so a key written by one can always
    /// be entered through another.
    /// </summary>
    public static string NormalizePassword(string? password) => password?.Trim() ?? string.Empty;
}
