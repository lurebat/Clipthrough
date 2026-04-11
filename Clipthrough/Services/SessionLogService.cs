using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Clipthrough.Models;

namespace Clipthrough.Services;

public sealed class SessionLogService : TraceListener, ISessionLogService
{
    private readonly object _gate = new();
    private readonly List<SessionLogEntry> _entries = [];
    private readonly Subject<SessionLogEntry> _entriesSubject = new();

    private SessionLogService()
    {
    }

    public static SessionLogService Instance { get; } = new();

    public IObservable<SessionLogEntry> Entries => _entriesSubject.AsObservable();

    public IReadOnlyList<SessionLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            AddEntry(AppNotificationLevel.Information, message);
        }
    }

    public override void TraceEvent(TraceEventCache? eventCache, string? source, TraceEventType eventType, int id, string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            AddEntry(ToLevel(eventType), message);
        }
    }

    public override void TraceEvent(TraceEventCache? eventCache, string? source, TraceEventType eventType, int id, string? format, params object?[]? args)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return;
        }

        var message = args is { Length: > 0 }
            ? string.Format(format, args)
            : format;

        if (!string.IsNullOrWhiteSpace(message))
        {
            AddEntry(ToLevel(eventType), message);
        }
    }

    private void AddEntry(AppNotificationLevel level, string message)
    {
        var entry = new SessionLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Level = level,
            Message = message.Trim(),
        };

        lock (_gate)
        {
            _entries.Add(entry);
        }

        _entriesSubject.OnNext(entry);
    }

    private static AppNotificationLevel ToLevel(TraceEventType eventType) => eventType switch
    {
        TraceEventType.Critical => AppNotificationLevel.Error,
        TraceEventType.Error => AppNotificationLevel.Error,
        TraceEventType.Warning => AppNotificationLevel.Warning,
        _ => AppNotificationLevel.Information,
    };
}
