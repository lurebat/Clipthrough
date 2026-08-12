using System;
using System.Reactive;
using ReactiveUI;

namespace Clipthrough.ViewModels;

public sealed class SensitivityRuleEditorViewModel : ViewModelBase
{
    private long _id;
    private string _name = string.Empty;
    private string _pattern = string.Empty;
    private string _severity = "warning";
    private bool _isEnabled = true;
    private bool _isBuiltIn = true;
    private bool _isExpanded;

    public SensitivityRuleEditorViewModel(Action<SensitivityRuleEditorViewModel>? removeHandler = null)
    {
        RemoveCommand = ReactiveCommand.Create(() => removeHandler?.Invoke(this));
        ToggleExpandedCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });
        ObserveCommandErrors();
    }

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    public ReactiveCommand<Unit, Unit> ToggleExpandedCommand { get; }

    public long Id
    {
        get => _id;
        set => this.RaiseAndSetIfChanged(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            this.RaiseAndSetIfChanged(ref _name, value);
            RaiseHeaderProperties();
        }
    }

    public string Pattern
    {
        get => _pattern;
        set => this.RaiseAndSetIfChanged(ref _pattern, value);
    }

    public string Severity
    {
        get => _severity;
        set
        {
            this.RaiseAndSetIfChanged(ref _severity, value);
            RaiseHeaderProperties();
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
    }

    public bool IsBuiltIn
    {
        get => _isBuiltIn;
        set
        {
            this.RaiseAndSetIfChanged(ref _isBuiltIn, value);
            RaiseHeaderProperties();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    public bool CanRemove => !IsBuiltIn;

    public string HeaderText => string.IsNullOrWhiteSpace(Name) ? "Custom rule" : Name;

    public string SummaryText => string.IsNullOrWhiteSpace(Pattern) ? "No pattern configured" : Severity;

    public void RaiseHeaderProperties()
    {
        this.RaisePropertyChanged(nameof(HeaderText));
        this.RaisePropertyChanged(nameof(SummaryText));
        this.RaisePropertyChanged(nameof(CanRemove));
    }
}
