using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Clipthrough;

/// <summary>
/// Parsed command-line options. Set once by <see cref="Program.Main"/> via
/// <see cref="Parse"/> and read later during startup and runtime diagnostics.
/// </summary>
public static class CommandLineOptions
{
    /// <summary>
    /// When set, the encrypted database is opened with this password without
    /// prompting. Intended for development / debugging only — supplying a
    /// password on the command line exposes it via process listings and shell
    /// history. A warning is traced when used.
    /// </summary>
    public static string? PresetDatabasePassword { get; private set; }

    /// <summary>
    /// When true, emits Stopwatch-based Trace lines around the popup show/hide
    /// path and the refresh pipeline so we can pinpoint freezes.
    /// </summary>
    public static bool LogPopupTimings { get; private set; }


    public const string DataDirectoryOption = "--data-dir";

    public const string UsageText =
        "Clipthrough — clipboard manager\n" +
        "\n" +
        "Usage:\n" +
        "  Clipthrough.exe [options]\n" +
        "\n" +
        "Options:\n" +
        "  -h, --help                 Show this help and exit.\n" +
        "  -p, --password <value>     Open the encrypted database with this password\n" +
        "                             without prompting. Intended for development only —\n" +
        "                             the value leaks into the process listing.\n" +
        "      --log-popup-timings    Emit Trace lines around the popup show/hide and\n" +
        "                             refresh pipeline so freezes can be diagnosed.\n" +
        "                             Alias: --log-timings.\n" +
        "      --data-dir <path>      Keep settings, logs and the library in <path>\n" +
        "                             instead of %LOCALAPPDATA%\\Clipthrough. Wins over\n" +
        "                             CLIPTHROUGH_DATA_DIR and a portable.flag marker.\n";

    /// <summary>
    /// Whether <c>--help</c>, <c>-h</c> or <c>/?</c> was passed. Read before any
    /// other startup work, so help prints even when the data folder is unusable.
    /// </summary>
    public static bool IsHelpRequested(IReadOnlyList<string> args) => Scan(args).Help;

    /// <summary>
    /// The value of <c>--data-dir</c>: null when the option is absent, empty
    /// when it is present without a folder. Separate from <see cref="Parse"/>
    /// because it is needed before tracing starts: the log file lives in the
    /// data folder it names.
    /// </summary>
    public static string? ReadDataDirectory(IReadOnlyList<string> args) => Scan(args).DataDirectory;

    // The options read before tracing starts, so without Parse's side effects.
    private static (bool Help, string? DataDirectory) Scan(IReadOnlyList<string> args)
    {
        var help = false;
        string? dataDirectory = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrEmpty(arg))
            {
                continue;
            }

            if (IsHelpOption(arg))
            {
                help = true;
                continue;
            }

            // Consume the password's value so it is never read as an option.
            if (TryReadValue(args, ref i, arg, "--password", "-p", out _))
            {
                continue;
            }

            // Present without a folder - last, or followed by another option -
            // is reported at startup rather than ignored.
            if (string.Equals(arg, DataDirectoryOption, StringComparison.OrdinalIgnoreCase)
                && (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal)))
            {
                dataDirectory = string.Empty;
                continue;
            }

            if (TryReadValue(args, ref i, arg, DataDirectoryOption, null, out var directory))
            {
                dataDirectory = directory;
            }
        }

        return (help, dataDirectory);
    }

    private static bool IsHelpOption(string arg)
        => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(arg, "-h", StringComparison.Ordinal)
            || string.Equals(arg, "/?", StringComparison.Ordinal);
    public static void Parse(string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrEmpty(arg))
            {
                continue;
            }


            if (TryReadValue(args, ref i, arg, "--password", "-p", out var password))
            {
                PresetDatabasePassword = password;
                Trace.TraceWarning(
                    "Database password supplied via --password CLI argument. " +
                    "This is intended for development/debugging only and leaks the " +
                    "password to the process list.");
                continue;
            }

            // Read by ReadDataDirectory before startup; consumed here so its
            // value is never mistaken for another option.
            if (TryReadValue(args, ref i, arg, DataDirectoryOption, null, out _))
            {
                continue;
            }

            if (string.Equals(arg, "--log-popup-timings", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--log-timings", StringComparison.OrdinalIgnoreCase))
            {
                LogPopupTimings = true;
                Trace.TraceInformation("Popup-timing diagnostics enabled (--log-popup-timings).");
                continue;
            }
        }
    }

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string current,
        string longName,
        string? shortName,
        out string value)
    {
        // --name=value
        if (current.StartsWith(longName + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = current[(longName.Length + 1)..];
            return true;
        }

        // --name value  /  -n value
        if (string.Equals(current, longName, StringComparison.OrdinalIgnoreCase)
            || (shortName is not null && string.Equals(current, shortName, StringComparison.Ordinal)))
        {
            if (index + 1 < args.Count)
            {
                index++;
                value = args[index];
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
