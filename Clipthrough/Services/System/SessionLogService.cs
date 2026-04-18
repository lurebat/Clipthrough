using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Clipthrough.Models;

namespace Clipthrough.Services;

public sealed class SessionLogService : TraceListener, ISessionLogService
{
    private static readonly string[] s_ignoredFrameworkMessageFragments =
    [
        "PlatformImpl is null, couldn't handle input.",
        "windows::UI::Composition::ICompositor5.RequestCommitAsync timed out, force-triggering next tick",
    ];

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
        if (!string.IsNullOrWhiteSpace(message) && !ShouldIgnoreMessage(message))
        {
            AddEntry(ClassifyMessage(message), message);
        }
    }

    private static AppNotificationLevel ClassifyMessage(string message)
    {
        // Avalonia's LogToTrace routes everything through Trace.WriteLine without a severity.
        // Default Avalonia log level is Warning, so untyped messages are at least warnings.
        // Binding failures and explicit "error" wording escalate to Error.
        if (message.Contains("error occurred", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Exception", StringComparison.Ordinal)
            || message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            return AppNotificationLevel.Error;
        }
        return AppNotificationLevel.Warning;
    }

    public override void TraceEvent(TraceEventCache? eventCache, string? source, TraceEventType eventType, int id, string? message)
    {
        if (!string.IsNullOrWhiteSpace(message) && !ShouldIgnoreMessage(message))
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

        if (!string.IsNullOrWhiteSpace(message) && !ShouldIgnoreMessage(message))
        {
            AddEntry(ToLevel(eventType), message);
        }
    }

    private static bool ShouldIgnoreMessage(string message)
        => Array.Exists(
            s_ignoredFrameworkMessageFragments,
            fragment => message.Contains(fragment, StringComparison.Ordinal));

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

        if (Dispatcher.UIThread.CheckAccess())
        {
            _entriesSubject.OnNext(entry);
            return;
        }

        Dispatcher.UIThread.Post(() => _entriesSubject.OnNext(entry));
    }

    private static AppNotificationLevel ToLevel(TraceEventType eventType) => eventType switch
    {
        TraceEventType.Critical => AppNotificationLevel.Error,
        TraceEventType.Error => AppNotificationLevel.Error,
        TraceEventType.Warning => AppNotificationLevel.Warning,
        _ => AppNotificationLevel.Information,
    };
}
