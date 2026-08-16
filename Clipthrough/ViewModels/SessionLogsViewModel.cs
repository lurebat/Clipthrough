using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
        _subscriptions.Add(ObserveCommandErrors());

        _subscriptions.Add(
            this.WhenAnyValue(x => x.SearchText, x => x.SelectedLogLevelOption)
                .Skip(1)
                .Subscribe(_ => RefreshVisibleSessionLogs()));

        _subscriptions.Add(
            sessionLogService.Entries
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(AddEntry));

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
                sb.AppendLine(CultureInfo.InvariantCulture, $"{log.DateText} {log.TimestampText}\t{log.LevelText}\t{message}");
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
        var item = new SessionLogEntryViewModel(entry);
        _allSessionLogs.Insert(0, item);
        if (_allSessionLogs.Count > SessionLogService.MaxRetainedEntries)
        {
            _allSessionLogs.RemoveRange(
                SessionLogService.MaxRetainedEntries,
                _allSessionLogs.Count - SessionLogService.MaxRetainedEntries);
        }

        // Nothing is bound to VisibleSessionLogs while the window is closed, and Open()
        // rebuilds it from scratch. This view model is constructed with the main window and
        // subscribed for the whole session, so without this gate every trace line from every
        // background worker paid for a bound-collection rebuild that nobody could see.
        if (!IsOpen)
        {
            return;
        }

        if (MatchesFilter(item))
        {
            // Insert, not rebuild. Clearing and refilling the collection raises one
            // notification per surviving row, so a session that logged n lines did O(n^2)
            // work and handed the list box O(n^2) change notifications.
            VisibleSessionLogs.Insert(0, item);
            while (VisibleSessionLogs.Count > SessionLogService.MaxRetainedEntries)
            {
                VisibleSessionLogs.RemoveAt(VisibleSessionLogs.Count - 1);
            }

            this.RaisePropertyChanged(nameof(HasLogs));
            this.RaisePropertyChanged(nameof(ShowEmptyState));
        }

        this.RaisePropertyChanged(nameof(CountText));
    }

    /// <summary>
    /// The single definition of "this entry belongs in the visible list". Shared so the
    /// incremental insert above and the full rebuild below cannot drift apart.
    /// </summary>
    private bool MatchesFilter(SessionLogEntryViewModel log)
    {
        var selectedLevel = SelectedLogLevelOption.Value;
        if (selectedLevel is not null && log.Entry.Level != selectedLevel.Value)
        {
            return false;
        }

        var searchText = SearchText.Trim();
        return searchText.Length == 0
            || log.Message.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshVisibleSessionLogs()
    {
        var filtered = _allSessionLogs.Where(MatchesFilter).ToArray();

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
