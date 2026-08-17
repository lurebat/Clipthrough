using System;

using ReactiveUI;

namespace Clipthrough.ViewModels;

/// <summary>
/// One collapsible section of the Settings form: whether the user has it open,
/// and whether it survives the current settings-search filter.
/// </summary>
/// <remarks>
/// The state of a section used to be spread across four parallel thirteen-entry
/// lists on <see cref="MainWindowViewModel"/> -- a keyword blob, an expansion
/// property, a visibility property, and a line in each of the two loops that
/// notify and auto-expand. Adding a section therefore meant five edits, and
/// three of them failed silently: forget the notification and the section stops
/// responding to the filter, forget the auto-expand and it matches but stays
/// shut. Holding a section's state in one object lets
/// <see cref="SettingsViewModel"/> iterate its sections instead of listing them.
/// </remarks>
public sealed class SettingsSectionViewModel : ViewModelBase
{
    private readonly Func<string, bool> _matchesFilter;

    internal SettingsSectionViewModel(string keywords, Func<string, bool> matchesFilter, bool isExpanded)
    {
        Keywords = keywords;
        _matchesFilter = matchesFilter;
        _isExpanded = isExpanded;
    }

    /// <summary>
    /// The words the settings search matches this section against. They are not
    /// the section's visible labels: the point is to find "dark mode" from
    /// "theme", so the blob deliberately includes synonyms and the names of
    /// third-party things a user might search for.
    /// </summary>
    public string Keywords { get; }

    private bool _isExpanded;

    /// <summary>Whether the section's content is showing.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    /// <summary>
    /// Whether the section matches the current settings-search filter. Computed
    /// rather than stored, so it cannot disagree with the filter; the owner
    /// calls <see cref="RaiseVisibilityChanged"/> when the filter moves.
    /// </summary>
    public bool IsVisible => _matchesFilter(Keywords);

    internal void RaiseVisibilityChanged() => this.RaisePropertyChanged(nameof(IsVisible));
}
