using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Clipthrough.Models;

namespace Clipthrough.Services;

public sealed class SessionLogService : TraceListener, ISessionLogService
{
    private static readonly string[] s_ignoredFrameworkMessageFragments =
    [
        "PlatformImpl is null, couldn't handle input.",
        "windows::UI::Composition::ICompositor5.RequestCommitAsync timed out, force-triggering next tick",
        "[Layout]Layout cycle detected. Item 'Avalonia.Controls.Primitives.DataGridRowsPresenter'",
        "[Layout]Layout cycle detected. Item 'Avalonia.Controls.Primitives.DataGridColumnHeadersPresenter'",
        "[Layout]Layout cycle detected. Item 'Avalonia.Controls.DataGrid'",
        "[Layout]Layout cycle detected. Item 'Avalonia.Controls.Grid'",
    ];

    private readonly object _gate = new();
    private readonly Queue<SessionLogEntry> _entries = new();
    private readonly Subject<SessionLogEntry> _entriesSubject = new();

    /// <summary>
    /// How many entries the in-memory session log keeps. Without a bound, a trace that
    /// repeats every frame - a layout cycle warning, say - grows the buffer for as long as
    /// the app runs. The log *file* still receives every line; only the in-app viewer's
    /// scrollback is bounded.
    /// </summary>
    public const int MaxRetainedEntries = 5_000;

    private SessionLogService()
    {
    }

    public static SessionLogService Instance { get; } = new();

    /// <summary>
    /// Entries as they are logged. Delivered on whichever thread wrote the trace - callers
    /// that need the UI thread must say so with <c>ObserveOn</c>. Marshalling here instead
    /// would wake the dispatcher once per trace line, and every trace line from every
    /// background worker goes through this.
    /// </summary>
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

        // Published under the same lock that appends, so subscribers see entries in the
        // order they were logged even when several threads trace at once - a Subject may
        // not have OnNext called concurrently. Subscribers marshal with ObserveOn, so what
        // runs here is a scheduler post, not their handler.
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > MaxRetainedEntries)
            {
                _entries.Dequeue();
            }

            _entriesSubject.OnNext(entry);
        }
    }

    private static AppNotificationLevel ToLevel(TraceEventType eventType) => eventType switch
    {
        TraceEventType.Critical => AppNotificationLevel.Error,
        TraceEventType.Error => AppNotificationLevel.Error,
        TraceEventType.Warning => AppNotificationLevel.Warning,
        _ => AppNotificationLevel.Information,
    };
}
