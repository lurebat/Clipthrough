using System;
using System.Collections.Generic;
using System.Linq;

namespace Clipthrough.Services;

/// <summary>
/// Lightweight fuzzy matcher used for best-effort search in settings and clips.
/// Combines substring match, synonym expansion, and Levenshtein-based token
/// scoring. No external dependencies.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Settings-specific synonyms. Keys are user terms; values are canonical
    /// keywords that appear in the settings keyword blobs.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> s_settingsSynonyms =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["hot key"] = new[] { "hotkey", "shortcut" },
            ["hotkeys"] = new[] { "hotkey", "shortcut" },
            ["shortcuts"] = new[] { "shortcut", "hotkey" },
            ["keybind"] = new[] { "hotkey", "shortcut" },
            ["keybinding"] = new[] { "hotkey", "shortcut" },
            ["keybindings"] = new[] { "hotkey", "shortcut" },
            ["dark mode"] = new[] { "theme", "dark" },
            ["light mode"] = new[] { "theme", "light" },
            ["color"] = new[] { "theme" },
            ["colour"] = new[] { "theme" },
            ["font"] = new[] { "appearance", "theme" },
            ["password"] = new[] { "password", "encryption" },
            ["encrypt"] = new[] { "encryption", "password" },
            ["db"] = new[] { "database", "sqlite", "storage" },
            ["sqlite"] = new[] { "database", "storage" },
            ["path"] = new[] { "path", "file", "location", "storage" },
            ["folder"] = new[] { "path", "location" },
            ["directory"] = new[] { "path", "location" },
            ["expire"] = new[] { "retention", "lifetime", "expire" },
            ["expiry"] = new[] { "retention", "lifetime" },
            ["cleanup"] = new[] { "retention", "lifetime" },
            ["cap"] = new[] { "capacity", "limit", "max" },
            ["max"] = new[] { "capacity", "limit" },
            ["limit"] = new[] { "capacity", "limit" },
            ["rule"] = new[] { "sensitivity", "rules", "pattern" },
            ["regex"] = new[] { "rules", "pattern", "regex" },
            ["secret"] = new[] { "sensitivity", "sensitive" },
            ["private"] = new[] { "sensitivity", "sensitive" },
            ["mask"] = new[] { "sensitivity", "sensitive" },
            ["diff"] = new[] { "tools", "diff", "compare" },
            ["compare"] = new[] { "tools", "diff", "compare" },
            ["editor"] = new[] { "tools", "editor" },
            ["external"] = new[] { "tools", "external" },
            ["tray"] = new[] { "tray", "behavior" },
            ["minimize"] = new[] { "tray", "minimize", "behavior" },
            ["startup"] = new[] { "startup", "start", "behavior" },
            ["autostart"] = new[] { "startup", "start" },
            ["boot"] = new[] { "startup", "start" },
            ["paste"] = new[] { "paste", "incremental", "decremental" },
            ["incremental"] = new[] { "incremental", "paste" },
            ["window"] = new[] { "window", "show", "hide" },
            ["show"] = new[] { "show", "window", "toggle" },
        };

    /// <summary>
    /// True if <paramref name="haystack"/> matches <paramref name="query"/>
    /// either as a substring, after synonym expansion, or via fuzzy token score.
    /// </summary>
    public static bool SettingsMatch(string haystack, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        query = query.Trim();
        if (haystack.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var expansion in ExpandSynonyms(query))
        {
            if (haystack.Contains(expansion, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return FuzzyContains(haystack, query, threshold: 0.72);
    }

    /// <summary>
    /// Computes a 0..1 similarity score using normalised Levenshtein distance,
    /// considering the query against each token in the haystack.
    /// </summary>
    public static double Score(string haystack, string query)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(query))
        {
            return 0.0;
        }

        if (haystack.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        var best = 0.0;
        foreach (var token in Tokenise(haystack))
        {
            var score = Ratio(token, query);
            if (score > best)
            {
                best = score;
            }
        }

        return best;
    }

    private static IEnumerable<string> ExpandSynonyms(string query)
    {
        if (s_settingsSynonyms.TryGetValue(query, out var direct))
        {
            foreach (var s in direct)
            {
                yield return s;
            }
        }

        foreach (var token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (s_settingsSynonyms.TryGetValue(token, out var expansions))
            {
                foreach (var s in expansions)
                {
                    yield return s;
                }
            }
        }
    }

    private static bool FuzzyContains(string haystack, string query, double threshold)
    {
        foreach (var token in Tokenise(haystack))
        {
            if (Ratio(token, query) >= threshold)
            {
                return true;
            }
        }

        // Try whole-query ratio against the whole haystack as a backstop.
        return Ratio(haystack, query) >= threshold;
    }

    private static IEnumerable<string> Tokenise(string text)
    {
        return text.Split(
            new[] { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '/', '-', '_' },
            StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static double Ratio(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return 0.0;
        }

        var max = Math.Max(a.Length, b.Length);
        if (max == 0)
        {
            return 1.0;
        }

        var distance = Levenshtein(a.ToLowerInvariant(), b.ToLowerInvariant());
        return 1.0 - ((double)distance / max);
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
