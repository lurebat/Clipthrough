using System;
using System.Collections.Generic;
using System.IO;
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
        var commandLine = CommandLineOptions.ReadDataDirectory(args);
        if (commandLine is not null)
        {
            return AppDataRoot.Chosen(ExpandPath(commandLine), AppDataRootSource.CommandLine);
        }

        if (!string.IsNullOrEmpty(environmentValue))
        {
            return AppDataRoot.Chosen(ExpandPath(environmentValue), AppDataRootSource.EnvironmentVariable);
        }

        if (File.Exists(Path.Combine(executableDirectory, PortableMarkerFileName)))
        {
            return AppDataRoot.Chosen(Path.Combine(executableDirectory, PortableDataDirectoryName), AppDataRootSource.Portable);
        }

        return AppDataRoot.Default(localApplicationData);
    }

    private static string ExpandPath(string value) => Environment.ExpandEnvironmentVariables(value.Trim());
}
