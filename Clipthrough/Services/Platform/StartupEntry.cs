using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Clipthrough.Services.Platform;

/// <summary>
/// The Start with Windows entry: one value per Windows user, shared by every
/// configuration of Clipthrough that user runs. A seam so the rules about whose
/// entry it is can be tested without the real registry.
/// </summary>
internal interface IStartupEntryStore
{
    /// <summary>The stored command, or null when there is none.</summary>
    string? Read();

    void Write(string command);

    void Delete();
}

/// <summary>HKCU\...\Run\Clipthrough.</summary>
[SupportedOSPlatform("windows")]
internal sealed class RunKeyStartupEntryStore : IStartupEntryStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Clipthrough";

    public string? Read()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return runKey?.GetValue(ValueName) as string;
    }

    public void Write(string command)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        runKey?.SetValue(ValueName, command, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        runKey?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>
/// Whether a stored Start with Windows command starts the same configuration
/// as another: the same executable and the same <c>--data-dir</c>. Both are
/// split the way Windows splits a command line, and the paths compared
/// normalized and case-insensitively, so a differently spelled entry for the
/// same configuration still counts as that configuration's.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StartupEntryCommand
{
    public static bool IsSameConfiguration(string stored, string command)
    {
        var storedArguments = Split(stored);
        var commandArguments = Split(command);
        if (storedArguments.Length == 0 || commandArguments.Length == 0)
        {
            return false;
        }

        return SamePath(storedArguments[0], commandArguments[0])
            && SameDataDirectory(DataDirectory(storedArguments), DataDirectory(commandArguments));
    }

    private static bool SameDataDirectory(string? left, string? right)
        => left is null || right is null ? left is null && right is null : SamePath(left, right);

    // The last --data-dir wins, as in CommandLineOptions.
    private static string? DataDirectory(string[] arguments)
    {
        string? value = null;
        for (var i = 1; i < arguments.Length; i++)
        {
            if (string.Equals(arguments[i], CommandLineOptions.DataDirectoryOption, StringComparison.OrdinalIgnoreCase))
            {
                value = i + 1 < arguments.Length ? arguments[++i] : string.Empty;
            }
            else if (arguments[i].StartsWith(CommandLineOptions.DataDirectoryOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arguments[i][(CommandLineOptions.DataDirectoryOption.Length + 1)..];
            }
        }

        return value;
    }

    private static bool SamePath(string left, string right)
        => string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    // CommandLineToArgvW answers an empty string with the current process's
    // own path, which would make an empty entry look like ours.
    private static string[] Split(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? string.Empty;
            }

            return result;
        }
        finally
        {
            _ = LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
