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

    /// <summary>
    /// Set when the user passed <c>--help</c> / <c>-h</c>. <see cref="Program.Main"/>
    /// prints the usage block and exits before any other startup work runs.
    /// </summary>
    public static bool ShowHelp { get; private set; }

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
        "                             Alias: --log-timings.\n";

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

            if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-h", StringComparison.Ordinal)
                || string.Equals(arg, "/?", StringComparison.Ordinal))
            {
                ShowHelp = true;
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
