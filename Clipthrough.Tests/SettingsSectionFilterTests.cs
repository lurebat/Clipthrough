using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

using Clipthrough.ViewModels;

using Xunit;

namespace Clipthrough.Tests;

/// <summary>
/// The Settings form's own search box: which sections it hides, and which it
/// opens. None of this was covered before it moved out of
/// <see cref="MainWindowViewModel"/>, which is what made the move risky -- the
/// behaviour was three hand-maintained parallel lists, and dropping an entry
/// from any of them produced silence rather than a failure.
/// </summary>
public sealed class SettingsSectionFilterTests
{
    private static IReadOnlyList<(string Name, SettingsSectionViewModel Section)> SectionsOf(SettingsViewModel vm)
        => typeof(SettingsViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(SettingsSectionViewModel))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => (p.Name, (SettingsSectionViewModel)p.GetValue(vm)!))
            .ToList();

    [Fact]
    public void EmptyFilterShowsEverySection()
    {
        var vm = new SettingsViewModel();

        Assert.NotEmpty(SectionsOf(vm));
        Assert.All(SectionsOf(vm), s => Assert.True(s.Section.IsVisible, $"{s.Name} should show when nothing is typed"));
    }

    [Fact]
    public void FilterHidesSectionsThatDoNotMatch()
    {
        var vm = new SettingsViewModel();

        vm.Filter = "winmerge";

        Assert.True(vm.ToolsSection.IsVisible);
        Assert.False(vm.BehaviorSection.IsVisible);
        Assert.False(vm.SemanticSection.IsVisible);
    }

    [Fact]
    public void ClearingTheFilterBringsEverySectionBack()
    {
        var vm = new SettingsViewModel();
        vm.Filter = "winmerge";
        Assert.False(vm.BehaviorSection.IsVisible);

        vm.Filter = string.Empty;

        Assert.All(SectionsOf(vm), s => Assert.True(s.Section.IsVisible, $"{s.Name} should come back"));
    }

    /// <summary>
    /// A section that matches is opened, so the hit the user searched for is on
    /// screen rather than behind a second click.
    /// </summary>
    [Fact]
    public void MatchingSectionIsExpandedEvenIfTheUserHadCollapsedIt()
    {
        var vm = new SettingsViewModel();
        vm.OcrSection.IsExpanded = false;

        vm.Filter = "ocr";

        Assert.True(vm.OcrSection.IsVisible);
        Assert.True(vm.OcrSection.IsExpanded);
    }

    /// <summary>
    /// The reverse: clearing the box must not fling every section open. An empty
    /// filter matches everything, so auto-expanding on it would discard whatever
    /// the user had deliberately collapsed.
    /// </summary>
    [Fact]
    public void ClearingTheFilterDoesNotReopenSectionsTheUserCollapsed()
    {
        var vm = new SettingsViewModel();
        vm.Filter = "ocr";
        vm.OcrSection.IsExpanded = false;

        vm.Filter = string.Empty;

        Assert.False(vm.OcrSection.IsExpanded);
    }

    /// <summary>
    /// The fuzzy toggle is what makes a search in the user's words rather than
    /// the developer's work: "dark mode" appears nowhere in the Behavior
    /// section's keywords, and only synonym expansion connects it to "theme".
    /// </summary>
    [Fact]
    public void FuzzyMatchingIsWhatFindsASectionByASynonym()
    {
        const string InTheUsersWords = "dark mode";

        var fuzzy = new SettingsViewModel { UseFuzzySearch = true, Filter = InTheUsersWords };
        var exact = new SettingsViewModel { UseFuzzySearch = false, Filter = InTheUsersWords };

        Assert.DoesNotContain(InTheUsersWords, fuzzy.BehaviorSection.Keywords, StringComparison.OrdinalIgnoreCase);
        Assert.True(fuzzy.BehaviorSection.IsVisible);
        Assert.False(exact.BehaviorSection.IsVisible);
    }

    /// <summary>
    /// The structural claim, and the reason the sections became objects: every
    /// section the view model exposes is re-evaluated when the filter moves.
    ///
    /// This used to be a thirteen-line list of <c>RaisePropertyChanged</c> calls
    /// beside a thirteen-line list of auto-expand assignments. A section added
    /// without a line in the first would render once and then ignore the filter
    /// forever, and nothing would say so. The test reflects over the sections
    /// rather than naming them, so it fails for a section that has been declared
    /// but not registered.
    /// </summary>
    [Fact]
    public void EverySectionIsNotifiedWhenTheFilterChanges()
    {
        var vm = new SettingsViewModel();
        var sections = SectionsOf(vm);
        var notified = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, section) in sections)
        {
            var captured = name;
            section.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsSectionViewModel.IsVisible))
                {
                    notified.Add(captured);
                }
            };
        }

        vm.Filter = "storage";

        var missing = sections.Select(s => s.Name).Where(n => !notified.Contains(n)).ToList();
        Assert.Empty(missing);
    }

    /// <summary>
    /// Toggling fuzzy matching changes which sections match, so it has to
    /// re-notify for the same reason typing does.
    /// </summary>
    [Fact]
    public void EverySectionIsNotifiedWhenFuzzyMatchingIsToggled()
    {
        var vm = new SettingsViewModel { Filter = "winmerg" };
        var sections = SectionsOf(vm);
        var notified = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, section) in sections)
        {
            var captured = name;
            section.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsSectionViewModel.IsVisible))
                {
                    notified.Add(captured);
                }
            };
        }

        vm.UseFuzzySearch = !vm.UseFuzzySearch;

        var missing = sections.Select(s => s.Name).Where(n => !notified.Contains(n)).ToList();
        Assert.Empty(missing);
    }

    /// <summary>
    /// Anti-vacuity for the two tests above. They would both pass if
    /// <c>IsVisible</c> were raised on every section on every change of any
    /// property, which would make the notification carry no information and
    /// would re-run the settings window's bindings on each keystroke elsewhere
    /// in the form. Editing an unrelated setting must not touch the sections.
    /// </summary>
    [Fact]
    public void EditingAnUnrelatedSettingDoesNotNotifyTheSections()
    {
        var vm = new SettingsViewModel();
        var raised = new List<string>();

        foreach (var (name, section) in SectionsOf(vm))
        {
            var captured = name;
            section.PropertyChanged += (_, e) => raised.Add($"{captured}.{e.PropertyName}");
        }

        vm.AiBaseUrl = "https://example.invalid/v1";
        vm.MaxEntryCount = "1234";

        Assert.Empty(raised);
    }

    /// <summary>
    /// Every section carries keywords, and no two sections share a blob. A
    /// copy-pasted section that kept the keywords of the one it was copied from
    /// would show and hide in lockstep with it, which reads as the filter being
    /// broken rather than as a duplicated string.
    /// </summary>
    [Fact]
    public void SectionKeywordsArePresentAndDistinct()
    {
        var sections = SectionsOf(new SettingsViewModel());

        Assert.All(sections, s => Assert.False(
            string.IsNullOrWhiteSpace(s.Section.Keywords),
            $"{s.Name} has no keywords, so the settings search can never find it"));

        var duplicated = sections
            .GroupBy(s => s.Section.Keywords, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" and ", g.Select(s => s.Name)))
            .ToList();

        Assert.Empty(duplicated);
    }
}
