using System;
using System.IO;

namespace Clipthrough.Models;

public sealed record StorageOptions
{
    public string DatabasePath { get; init; } = GetDefaultDatabasePath();

    public string DatabasePassword { get; init; } = string.Empty;

    public static StorageOptions Default { get; } = new();

    public StorageOptions Normalize()
    {
        var normalizedPath = string.IsNullOrWhiteSpace(DatabasePath)
            ? GetDefaultDatabasePath()
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(DatabasePath.Trim()));

        return this with
        {
            DatabasePath = normalizedPath,
            DatabasePassword = DatabasePassword?.Trim() ?? string.Empty,
        };
    }

    public static string GetDefaultDatabasePath()
    {
        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipthrough");

        return Path.Combine(appDataDirectory, "clipthrough.db");
    }
}
