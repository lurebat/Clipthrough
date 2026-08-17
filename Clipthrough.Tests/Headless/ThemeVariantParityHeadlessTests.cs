using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;

using Xunit;

namespace Clipthrough.Tests.Headless;

/// <summary>
/// Every themed brush has to exist under both variants.
///
/// This is the failure mode that only breaks for people who are not you: a brush
/// added to the Dark dictionary and forgotten in Light resolves fine for every
/// developer running dark mode, and comes out unstyled for the users who are not.
/// The two dictionaries are a pair of parallel lists kept in step by hand, and
/// nothing was checking them.
/// </summary>
public sealed class ThemeVariantParityHeadlessTests
{
    private static IEnumerable<(string Owner, IResourceDictionary Dark, IResourceDictionary Light)> ThemedDictionaries()
    {
        var app = Application.Current;
        Assert.NotNull(app);

        var index = 0;
        foreach (var style in app!.Styles)
        {
            index++;
            if (style is not Styles styles)
            {
                continue;
            }

            var dictionaries = styles.Resources.ThemeDictionaries;
            if (!dictionaries.TryGetValue(ThemeVariant.Dark, out var dark)
                || !dictionaries.TryGetValue(ThemeVariant.Light, out var light))
            {
                continue;
            }

            if (dark is IResourceDictionary d && light is IResourceDictionary l)
            {
                yield return ($"Styles[{index - 1}]", d, l);
            }
        }
    }

    private static IReadOnlyCollection<string> KeysOf(IResourceDictionary dictionary)
        => dictionary.Keys.Select(k => k.ToString() ?? string.Empty).ToHashSet(StringComparer.Ordinal);

    [AvaloniaFact]
    public void EveryThemedResourceIsDefinedUnderBothVariants()
    {
        var themed = ThemedDictionaries().ToList();

        // Without this the test would pass on an application whose styles were
        // not loaded, having compared nothing at all.
        Assert.NotEmpty(themed);

        var problems = new List<string>();
        foreach (var (owner, dark, light) in themed)
        {
            var darkKeys = KeysOf(dark);
            var lightKeys = KeysOf(light);

            foreach (var missing in darkKeys.Except(lightKeys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
            {
                problems.Add($"{owner}: '{missing}' is defined for Dark but not Light");
            }

            foreach (var missing in lightKeys.Except(darkKeys, StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
            {
                problems.Add($"{owner}: '{missing}' is defined for Light but not Dark");
            }
        }

        Assert.Empty(problems);
    }

    /// <summary>
    /// Anti-vacuity for the comparison above: it passes trivially if the
    /// dictionaries turn out to be empty, so at least one has to carry a real
    /// number of entries.
    /// </summary>
    [AvaloniaFact]
    public void TheThemedDictionariesAreNotEmpty()
    {
        var largest = ThemedDictionaries()
            .Select(t => KeysOf(t.Dark).Count)
            .DefaultIfEmpty(0)
            .Max();

        Assert.True(largest >= 20, $"expected a themed dictionary with real content, largest had {largest} keys");
    }
}
