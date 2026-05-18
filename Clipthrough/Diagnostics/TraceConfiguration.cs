using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Clipthrough.Services;

namespace Clipthrough.Diagnostics;

public static class TraceConfiguration
{
    private const long MaxLogFileBytes = 2 * 1024 * 1024;
    private static bool s_initialized;

    /// <summary>
    /// Framework-noise fragments that get dropped before being written to the
    /// session log file. These are routine framework messages (composition
    /// retries, layout cycles, Avalonia input shutdown chatter) that fire many
    /// times per session and drown out actionable entries.
    /// </summary>
    private static readonly string[] s_ignoredNoiseFragments =
    [
        "PlatformImpl is null, couldn't handle input.",
        "windows::UI::Composition::ICompositor5.RequestCommitAsync timed out, force-triggering next tick",
        "[Layout]Layout cycle detected. Item 'Avalonia.Controls.Primitives.DataGridRowsPresenter'",
        "[Layout]Layout cycle detected. Item 'Avalonia.Controls.Primitives.DataGridColumnHeadersPresenter'",
        "[Layout]Layout cycle detected. Item 'Avalonia.Controls.DataGrid'",
        "[Layout]Layout cycle detected. Item 'Avalonia.Controls.Grid'",
    ];

    public static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clipthrough",
        "logs",
        "clipthrough.log");

    public static void Initialize()
    {
        if (s_initialized)
        {
            return;
        }

        s_initialized = true;

        var logDirectory = Path.GetDirectoryName(LogFilePath)!;
        Directory.CreateDirectory(logDirectory);
        RotateLogIfNeeded(LogFilePath);

        var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream) { AutoFlush = true };
        Trace.Listeners.Add(SessionLogService.Instance);
        Trace.Listeners.Add(new FilteringTraceListener(writer, "clipthrough-file", s_ignoredNoiseFragments));
        Trace.AutoFlush = true;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Trace.TraceInformation($"Trace logging initialized. Log file: {LogFilePath}");
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => Trace.TraceError($"Unhandled exception: {e.ExceptionObject}");

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Trace.TraceError($"Unobserved task exception: {e.Exception}");
        e.SetObserved();
    }

    private static void RotateLogIfNeeded(string logFilePath)
    {
        if (!File.Exists(logFilePath))
        {
            return;
        }

        var fileInfo = new FileInfo(logFilePath);
        if (fileInfo.Length < MaxLogFileBytes)
        {
            return;
        }

        var archivedPath = Path.Combine(
            fileInfo.DirectoryName!,
            $"clipthrough-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.log");

        File.Move(logFilePath, archivedPath, overwrite: true);
    }

    /// <summary>
    /// <see cref="TextWriterTraceListener"/> that suppresses lines containing any
    /// of the provided noise fragments. Used so the log file does not get
    /// flooded with framework chatter (compositor retries, layout cycles, etc.)
    /// that would otherwise drown out real warnings and errors.
    /// </summary>
    private sealed class FilteringTraceListener : TextWriterTraceListener
    {
        private readonly string[] _noiseFragments;

        public FilteringTraceListener(StreamWriter writer, string name, string[] noiseFragments)
            : base(writer, name)
        {
            _noiseFragments = noiseFragments;
        }

        public override void Write(string? message)
        {
            if (ShouldDrop(message)) return;
            base.Write(message);
        }

        public override void WriteLine(string? message)
        {
            if (ShouldDrop(message)) return;
            base.WriteLine(message);
        }

        public override void TraceEvent(TraceEventCache? eventCache, string? source, TraceEventType eventType, int id, string? message)
        {
            if (ShouldDrop(message)) return;
            base.TraceEvent(eventCache, source ?? string.Empty, eventType, id, message);
        }

        public override void TraceEvent(TraceEventCache? eventCache, string? source, TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            // Render once so we can inspect the final text before dropping.
            var rendered = format is null
                ? null
                : args is { Length: > 0 } ? string.Format(format, args) : format;
            if (ShouldDrop(rendered)) return;
            base.TraceEvent(eventCache, source ?? string.Empty, eventType, id, rendered);
        }

        private bool ShouldDrop(string? message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            foreach (var fragment in _noiseFragments)
            {
                if (message.Contains(fragment, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
