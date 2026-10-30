using System;
using System.Collections.Generic;
using System.IO;
using Clipthrough.Localization;
using Clipthrough.Models;

namespace Clipthrough.Services;

/// <summary>
/// Decides the <see cref="AppDataRoot"/> once, at startup. The only code that
/// may ask Windows where local application data lives:
/// <c>AppDataRootGuardTests</c> fails on any other use.
/// </summary>
/// <remarks>
/// Precedence, first match wins: the <c>--data-dir</c> command-line option, the
/// <c>CLIPTHROUGH_DATA_DIR</c> environment variable, a <c>portable.flag</c>
/// marker beside the executable, and finally %LOCALAPPDATA%\Clipthrough.
/// </remarks>
public static class AppDataRootResolver
{
    public const string EnvironmentVariable = "CLIPTHROUGH_DATA_DIR";

    /// <summary>The marker file that turns on portable mode. Its content is ignored.</summary>
    public const string PortableMarkerFileName = "portable.flag";

    /// <summary>The folder, beside the marker, that a portable copy keeps its data in.</summary>
    public const string PortableDataDirectoryName = "ClipthroughData";

    private const string VelopackUpdateExeName = "Update.exe";
    private const string VelopackManifestName = "sq.version";
    public static AppDataRoot ResolveForCurrentProcess(IReadOnlyList<string> args)
        => Resolve(
            args,
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    public static AppDataRoot Resolve(
        IReadOnlyList<string> args,
        string? environmentValue,
        string executableDirectory,
        string localApplicationData)
    {
        // A source that is present decides, whether or not it works: an
        // unusable choice is reported, never passed over for the next one.
        var commandLine = CommandLineOptions.ReadDataDirectory(args);
        if (commandLine is not null)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                throw new AppDataRootException(AppText.DataFolderMissingValue);
            }

            return Choose(commandLine, AppDataRootSource.CommandLine);
        }

        // Empty is how an unset variable reads on some platforms; blank is a mistake.
        if (!string.IsNullOrEmpty(environmentValue))
        {
            if (string.IsNullOrWhiteSpace(environmentValue))
            {
                throw new AppDataRootException(AppText.FormatDataFolderBlank(AppDataRootSource.EnvironmentVariable));
            }

            return Choose(environmentValue, AppDataRootSource.EnvironmentVariable);
        }

        var applicationRoot = FindApplicationRoot(executableDirectory);
        if (File.Exists(Path.Combine(applicationRoot, PortableMarkerFileName)))
        {
            return AppDataRoot.Chosen(Path.Combine(applicationRoot, PortableDataDirectoryName), AppDataRootSource.Portable);
        }

        return AppDataRoot.Default(localApplicationData);
    }

    /// <summary>
    /// The folder a user sees as "where Clipthrough is": the executable's own
    /// folder for a plain ZIP, but for a Velopack package - the installed app
    /// and the -Portable.zip alike - the folder above current\, beside
    /// Update.exe and the launcher stub. Velopack replaces current\ on every
    /// update, so a marker or data kept inside it would be deleted by one.
    /// The rule is Velopack's own (WindowsVelopackLocator, 1.2.0): Update.exe
    /// in the parent and the sq.version manifest beside the executable.
    /// </summary>
    internal static string FindApplicationRoot(string executableDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(executableDirectory));
        var parent = Path.GetDirectoryName(directory);
        return parent is not null
            && File.Exists(Path.Combine(parent, VelopackUpdateExeName))
            && File.Exists(Path.Combine(directory, VelopackManifestName))
            ? parent
            : directory;
    }
    private static AppDataRoot Choose(string value, AppDataRootSource source)
    {
        // Checked before expanding too: the expansion is a Win32 call, and stops at a NUL.
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || expanded.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new AppDataRootException(AppText.FormatDataFolderInvalid(value, source, AppText.DataFolderInvalidCharacters));
        }

        try
        {
            return AppDataRoot.Chosen(expanded, source);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new AppDataRootException(AppText.FormatDataFolderInvalid(value, source, ex.Message), ex);
        }
    }

    /// <summary>
    /// Creates the folder if needed and proves a file can be written in it, so
    /// an unusable folder stops startup with a message instead of failing
    /// later - or worse, half-working - in whichever service writes first.
    /// Throws <see cref="AppDataRootException"/> when it cannot be used.
    /// </summary>
    public static void EnsureUsable(AppDataRoot dataRoot)
    {
        try
        {
            Directory.CreateDirectory(dataRoot.Directory);
            var probe = Path.Combine(dataRoot.Directory, $".write-test-{Guid.NewGuid():N}.tmp");
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            throw new AppDataRootException(AppText.FormatDataFolderUnusable(dataRoot.Directory, dataRoot.Source, ex.Message), ex);
        }
    }
}
