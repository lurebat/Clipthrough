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
        Trace.Listeners.Add(new TextWriterTraceListener(writer, "clipthrough-file"));
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
}
