using System;
using System.Collections.Generic;
using System.IO;

namespace Clipthrough.Models;

/// <summary>Which of the ways to choose a data folder decided this one.</summary>
public enum AppDataRootSource
{
    Default,
    CommandLine,
    EnvironmentVariable,
    Portable,
}

/// <summary>
/// The folder Clipthrough keeps its own files in: settings, storage location,
/// window placement, the Copilot token and the logs, and - unless the user
/// moved it - the library. Resolved once at startup by
/// <c>AppDataRootResolver</c> and injected; nothing else decides where these go.
/// </summary>
/// <remarks>
/// The default root splits in two for a historical reason: a Velopack install
/// lives in %LOCALAPPDATA%\Clipthrough and uninstalling deletes that folder, so
/// the library moved to the sibling ClipthroughData. A chosen root has no
/// installer behind it, so everything lives inside it.
/// </remarks>
public sealed record AppDataRoot
{
    public const string DatabaseFileName = "clipthrough.db";
    private const string DefaultDirectoryName = "Clipthrough";
    private const string DefaultLibraryDirectoryName = "ClipthroughData";

    private AppDataRoot(string directory, AppDataRootSource source, string defaultDatabasePath, string? legacyDatabasePath)
    {
        Directory = directory;
        Source = source;
        DefaultDatabasePath = defaultDatabasePath;
        LegacyDatabasePath = legacyDatabasePath;
    }

    public string Directory { get; }

    public AppDataRootSource Source { get; }

    /// <summary>Where the library is created when the user has not chosen a location.</summary>
    public string DefaultDatabasePath { get; }

    /// <summary>
    /// Where versions before 0.6.3 kept the library, copied into
    /// <see cref="DefaultDatabasePath"/> on first launch. Only the default root
    /// had such a version, so a chosen root has none.
    /// </summary>
    public string? LegacyDatabasePath { get; }

    public string SettingsPath => Path.Combine(Directory, "settings.json");

    public string StorageConfigPath => Path.Combine(Directory, "storage.json");

    public string WindowPlacementPath => Path.Combine(Directory, "window.json");

    public string CopilotTokenPath => Path.Combine(Directory, "copilot-token.bin");

    public string LogFilePath => Path.Combine(Directory, "logs", "clipthrough.log");

    /// <summary>
    /// What to pass a relaunch of this process - Velopack's restart after an
    /// update - so it comes back to this folder. The variable and the portable
    /// marker survive a restart by themselves; only the command line does not.
    /// </summary>
    public IReadOnlyList<string> RelaunchArguments => Source == AppDataRootSource.CommandLine
        ? [CommandLineOptions.DataDirectoryOption, Directory]
        : [];

    /// <summary>
    /// The form of <paramref name="fullPath"/> to write to storage.json. In
    /// portable mode a path inside the folder is written relative to it, so the
    /// library is still found after the folder moves to another drive letter
    /// or parent; <see cref="StorageOptions.Normalize"/> resolves it back
    /// against <see cref="Directory"/>. Every other path stays as it was given.
    /// </summary>
    public string ToStoredPath(string fullPath)
    {
        if (Source != AppDataRootSource.Portable)
        {
            return fullPath;
        }

        var relative = Path.GetRelativePath(Directory, fullPath);
        return Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? fullPath
            : relative;
    }

    /// <summary>The root used when nothing chose another one.</summary>
    public static AppDataRoot Default(string localApplicationData)
        => new(
            Path.Combine(localApplicationData, DefaultDirectoryName),
            AppDataRootSource.Default,
            Path.Combine(localApplicationData, DefaultLibraryDirectoryName, DatabaseFileName),
            Path.Combine(localApplicationData, DefaultDirectoryName, DatabaseFileName));

    /// <summary>A root the user chose; everything, the library included, lives inside it.</summary>
    public static AppDataRoot Chosen(string directory, AppDataRootSource source)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return new(full, source, Path.Combine(full, DatabaseFileName), null);
    }
}
