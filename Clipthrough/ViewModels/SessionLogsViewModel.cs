using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using Clipthrough.Localization;
using Clipthrough.Models;
using Clipthrough.Services;
using ReactiveUI;

namespace Clipthrough.ViewModels;

public sealed class SessionLogsViewModel : ViewModelBase, IDisposable
{
    private readonly CompositeDisposable _subscriptions = new();
    private readonly List<SessionLogEntryViewModel> _allSessionLogs = [];
    private string _searchText = string.Empty;
    private LogLevelOption _selectedLogLevelOption;
    private bool _isOpen;

    public SessionLogsViewModel(ISessionLogService sessionLogService)
    {
        LogLevelOptions =
        [
            new LogLevelOption(null),
            new LogLevelOption(AppNotificationLevel.Information),
            new LogLevelOption(AppNotificationLevel.Warning),
            new LogLevelOption(AppNotificationLevel.Error),
        ];
        _selectedLogLevelOption = LogLevelOptions[0];

        OpenCommand = ReactiveCommand.Create(Open);
        CloseCommand = ReactiveCommand.Create(Close);

        _subscriptions.Add(
            this.WhenAnyValue(x => x.SearchText, x => x.SelectedLogLevelOption)
                .Skip(1)
                .Subscribe(_ => RefreshVisibleSessionLogs()));

        _subscriptions.Add(
            sessionLogService.Entries.Subscribe(AddEntry));

        foreach (var entry in sessionLogService.Snapshot())
        {
            AddEntry(entry);
        }
    }

    public ObservableCollection<SessionLogEntryViewModel> VisibleSessionLogs { get; } = [];

    public IReadOnlyList<LogLevelOption> LogLevelOptions { get; }

    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    public string AllLogsAsText
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine("Time\tLevel\tMessage");
            foreach (var log in VisibleSessionLogs)
            {
                var message = log.Message.Replace("\r\n", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
                sb.AppendLine($"{log.DateText} {log.TimestampText}\t{log.LevelText}\t{message}");
            }

            return sb.ToString();
        }
    }

    public string TitleText => AppText.LogsTitleText;

    public string DescriptionText => AppText.LogsDescriptionText;

    public string SearchWatermark => AppText.LogsSearchWatermark;

    public string EmptyMessage => AppText.NoLogsMatchFilters;

    public string CloseButtonLabel => AppText.CloseButtonLabel;

    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    public LogLevelOption SelectedLogLevelOption
    {
        get => _selectedLogLevelOption;
        set => this.RaiseAndSetIfChanged(ref _selectedLogLevelOption, value);
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => this.RaiseAndSetIfChanged(ref _isOpen, value);
    }

    public bool HasLogs => VisibleSessionLogs.Count > 0;

    public bool ShowEmptyState => !HasLogs;

    public string CountText => AppText.FormatLogCount(_allSessionLogs.Count);

    public void Open()
    {
        IsOpen = true;
        RefreshVisibleSessionLogs();
    }

    public void Close()
    {
        IsOpen = false;
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
    }

    private void AddEntry(SessionLogEntry entry)
    {
        _allSessionLogs.Insert(0, new SessionLogEntryViewModel(entry));
        RefreshVisibleSessionLogs();
    }

    private void RefreshVisibleSessionLogs()
    {
        var searchText = SearchText.Trim();
        var selectedLevel = SelectedLogLevelOption.Value;

        var filtered = _allSessionLogs
            .Where(log => selectedLevel is null || log.Entry.Level == selectedLevel.Value)
            .Where(log => searchText.Length == 0 || log.Message.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        VisibleSessionLogs.Clear();
        foreach (var log in filtered)
        {
            VisibleSessionLogs.Add(log);
        }

        this.RaisePropertyChanged(nameof(HasLogs));
        this.RaisePropertyChanged(nameof(ShowEmptyState));
        this.RaisePropertyChanged(nameof(CountText));
    }
}
