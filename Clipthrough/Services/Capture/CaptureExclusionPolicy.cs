using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Clipthrough.Services;

/// <summary>
/// Decides whether a clipboard change should be dropped because the application
/// that produced it is on the user's exclusion list — a password manager, a
/// banking app, an internal tool whose contents must never reach the history.
/// </summary>
/// <remarks>
/// A pattern matches an application when it equals, case-insensitively, any of:
/// the resolved friendly/process name, the executable file name, the file name
/// without its extension, or the full executable path. A pattern containing
/// <c>*</c> or <c>?</c> is instead glob-matched against those same candidates,
/// so <c>*keepass*</c> catches every KeePass variant and
/// <c>C:\Program Files\Vault\*</c> catches a whole install directory.
///
/// The policy deliberately <em>fails open</em>: when the source application
/// cannot be resolved at all, the clip is captured. Failing closed would mean
/// silently discarding every clipboard change whose owner window is hidden,
/// already gone, or owned by a protected process — a far more common situation
/// than an excluded app, and one that would destroy history the user expected
/// to keep. Exclusion is therefore best-effort, and the settings UI says so.
/// Apps that need a hard guarantee should publish the standard
/// <c>Clipboard Viewer Ignore</c> format, which this monitor already honours
/// regardless of the exclusion list.
/// </remarks>
public static class CaptureExclusionPolicy
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public static bool IsExcluded(ClipboardSourceApplicationInfo? source, IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0 || source is null)
        {
            return false;
        }

        var path = string.IsNullOrWhiteSpace(source.Path) ? null : source.Path.Trim();
        var fileName = path is null ? null : Path.GetFileName(path);
        var bareName = fileName is null ? null : Path.GetFileNameWithoutExtension(fileName);
        var friendlyName = string.IsNullOrWhiteSpace(source.Name) ? null : source.Name.Trim();

        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var pattern = raw.Trim();
            if (Matches(pattern, friendlyName)
                || Matches(pattern, fileName)
                || Matches(pattern, bareName)
                || Matches(pattern, path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits the newline-separated text the settings form edits into patterns,
    /// dropping blanks and duplicates while preserving the user's ordering.
    /// </summary>
    public static IReadOnlyList<string> ParsePatterns(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    public static string FormatPatterns(IReadOnlyList<string>? patterns) =>
        patterns is null || patterns.Count == 0 ? string.Empty : string.Join(Environment.NewLine, patterns);

    private static bool Matches(string pattern, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        if (!pattern.Contains('*', StringComparison.Ordinal) && !pattern.Contains('?', StringComparison.Ordinal))
        {
            return string.Equals(pattern, candidate, StringComparison.OrdinalIgnoreCase);
        }

        // Not FileSystemName.MatchesSimpleExpression: that treats '\' as an
        // escape character, so a directory pattern like C:\Tools\* never
        // matches a Windows path. Translate the glob ourselves instead.
        // Compiling per call is fine - this runs once per clipboard change
        // against a handful of user-authored patterns, and is invisible next
        // to the COM clipboard read it guards.
        return Regex.IsMatch(
            candidate,
            GlobToRegex(pattern),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            RegexTimeout);
    }

    private static string GlobToRegex(string pattern) =>
        "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
}
