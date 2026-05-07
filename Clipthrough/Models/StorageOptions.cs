using System;
using System.IO;

namespace Clipthrough.Models;

public sealed record StorageOptions
{
    private const string UserDataDirectoryName = "ClipthroughData";
    private const string LegacyUserDataDirectoryName = "Clipthrough";
    private const string DatabaseFileName = "clipthrough.db";

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
            UserDataDirectoryName);

        return Path.Combine(appDataDirectory, DatabaseFileName);
    }

    public static string GetLegacyDefaultDatabasePath()
    {
        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyUserDataDirectoryName);

        return Path.Combine(appDataDirectory, DatabaseFileName);
    }

    public static bool IsLegacyDefaultDatabasePath(string? databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(databasePath.Trim()));
        return string.Equals(normalizedPath, GetLegacyDefaultDatabasePath(), StringComparison.OrdinalIgnoreCase);
    }
}
