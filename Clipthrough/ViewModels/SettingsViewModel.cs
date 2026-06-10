using System;
using System.Collections.Generic;
using Clipthrough.Models;
using ReactiveUI;

namespace Clipthrough.ViewModels;

/// <summary>
/// Editable draft state for the Settings form, extracted from
/// <see cref="MainWindowViewModel"/> (#10). Holds the form values bound by
/// <c>SettingsWindow.axaml</c>; the host view model's <c>LoadSettingsDraft</c>
/// populates it and <c>SaveSettingsAsync</c> applies it (the storage-lifecycle
/// apply stays in the host). Grown one settings section per commit — currently
/// the AI section.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    // --- AI ---

    private bool _enableAi = AppSettings.Default.EnableAi;
    public bool EnableAi
    {
        get => _enableAi;
        set => this.RaiseAndSetIfChanged(ref _enableAi, value);
    }

    private AiProvider _aiProvider = AppSettings.Default.AiProvider;
    public AiProvider AiProvider
    {
        get => _aiProvider;
        set
        {
            this.RaiseAndSetIfChanged(ref _aiProvider, value);
            this.RaisePropertyChanged(nameof(IsOpenAiSettingsVisible));
            this.RaisePropertyChanged(nameof(IsCopilotSettingsVisible));
        }
    }

    public bool IsOpenAiSettingsVisible => AiProvider == Models.AiProvider.OpenAi;
    public bool IsCopilotSettingsVisible => AiProvider == Models.AiProvider.Copilot;

    private string _aiBaseUrl = AppSettings.Default.AiBaseUrl;
    public string AiBaseUrl
    {
        get => _aiBaseUrl;
        set => this.RaiseAndSetIfChanged(ref _aiBaseUrl, value);
    }

    private string _aiApiKey = AppSettings.Default.AiApiKey;
    public string AiApiKey
    {
        get => _aiApiKey;
        set => this.RaiseAndSetIfChanged(ref _aiApiKey, value);
    }

    private string _aiModel = AppSettings.Default.AiModel;
    public string AiModel
    {
        get => _aiModel;
        set => this.RaiseAndSetIfChanged(ref _aiModel, value);
    }

    private string _aiImageModel = AppSettings.Default.AiImageModel;
    public string AiImageModel
    {
        get => _aiImageModel;
        set => this.RaiseAndSetIfChanged(ref _aiImageModel, value);
    }

    private string _aiReasoningEffort = AppSettings.Default.AiReasoningEffort;
    public string AiReasoningEffort
    {
        get => _aiReasoningEffort;
        set => this.RaiseAndSetIfChanged(ref _aiReasoningEffort, value);
    }

    public IReadOnlyList<string> AiReasoningEffortOptions { get; } = new[] { "", "none", "minimal", "low", "medium", "high" };

    public AiProvider[] AiProviderOptions { get; } = Enum.GetValues<AiProvider>();
}
